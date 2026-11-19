// LocalNetworkMonitoring.cs: the code behind Jarvis's "what is on my network" answers. GetStatusAsync is the only entry
// point; it returns a block of text. It actively probes the home/office network: it lists this PC's active adapters, pings
// nearby addresses and looks up their host names, or inspects one host (ping and reverse DNS).
// The sweep only targets private addresses on the local network (host names typed by the user are looked up in DNS).
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Jarvis.Core;

internal static class LocalNetworkMonitoring
{
    // How long to wait for a ping reply before treating a device as absent. Short, because most addresses are empty.
    private const int PingTimeoutMilliseconds = 325;
    // How many pings run at the same time while sweeping a subnet (keeps a /24 scan to a few seconds).
    private const int MaxConcurrentPings = 48;

    // Whole phrases that ask for a mode rather than naming a device. NetworkTool matches what the user said against them, and
    // ParseRequest matches the target text it receives against "network <target>" (so the target "health" is the phrase "network health").
    internal static readonly IReadOnlyDictionary<string, NetworkRequestMode> ModePhrases =
        new Dictionary<string, NetworkRequestMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["network"] = NetworkRequestMode.Overview,
            ["local network"] = NetworkRequestMode.Overview,
            // The old scan-mode phrasings now get the overview, which still lists the devices it can reach.
            ["network scan"] = NetworkRequestMode.Overview,
            ["network devices"] = NetworkRequestMode.Overview,
            ["scan my network"] = NetworkRequestMode.Overview,
            ["discover devices on my network"] = NetworkRequestMode.Overview,
            ["network health"] = NetworkRequestMode.Health,
            ["network status"] = NetworkRequestMode.Health,
            ["check network health"] = NetworkRequestMode.Health,
            ["check my network"] = NetworkRequestMode.Health
        };

    // Main entry point. An empty target gives an overview, "health"/"status" gives the health wording,
    // and anything else (an IP address or host name) inspects that one device.
    public static async Task<string> GetStatusAsync(string target, CancellationToken cancellationToken)
    {
        var request = ParseRequest(target);

        if (request.Mode == NetworkRequestMode.Inspect && !string.IsNullOrWhiteSpace(request.Target))
        {
            return await InspectTargetAsync(request.Target, cancellationToken);
        }

        var adapters = GetActiveAdapters();

        if (adapters.Count == 0)
        {
            return "No active IPv4 local-network adapters were detected.";
        }

        var snapshot = await BuildSnapshotAsync(adapters, cancellationToken);
        return RenderSnapshot(snapshot, request.Mode);
    }

    // Decides which of the modes above the typed text means.
    private static NetworkRequest ParseRequest(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new NetworkRequest(NetworkRequestMode.Overview, string.Empty);
        }

        var trimmed = target.Trim();

        return ModePhrases.TryGetValue("network " + trimmed, out var mode)
            ? new NetworkRequest(mode, string.Empty)
            : new NetworkRequest(NetworkRequestMode.Inspect, trimmed);
    }

    // Sweeps the network once: works out which addresses to try, probes them, and packages the adapters and devices found.
    private static async Task<NetworkSnapshot> BuildSnapshotAsync(
        IReadOnlyList<ActiveAdapter> adapters,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var candidates = BuildScanCandidates(adapters);
        var discovered = await ProbeCandidatesAsync(candidates, adapters, cancellationToken);
        var elapsed = DateTimeOffset.UtcNow - startedUtc;

        foreach (var adapter in adapters)
        {
            foreach (var gateway in adapter.Gateways)
            {
                // Marks whether the router answered the sweep (used for the healthy / degraded label).
                adapter.GatewayResponded = discovered.Any(device => device.Address.Equals(gateway));
            }
        }

        return new NetworkSnapshot(
            adapters.ToArray(),
            discovered
                .OrderByDescending(device => device.IsGateway)
                .ThenBy(device => device.RoundtripMilliseconds ?? long.MaxValue)
                .ThenBy(device => device.Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            candidates.Count,
            elapsed);
    }

    // Lists the network adapters that are up and usable (skips loopback and tunnels), best first: ones with a gateway, then fastest.
    private static IReadOnlyList<ActiveAdapter> GetActiveAdapters()
    {
        var adapters = new List<ActiveAdapter>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (!TryCreateAdapter(networkInterface, out var adapter))
            {
                continue;
            }

            adapters.Add(adapter);
        }

        return adapters
            .OrderByDescending(adapter => adapter.HasGateway)
            .ThenByDescending(adapter => adapter.SpeedBitsPerSecond)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Reads one adapter's settings. Returns false unless it has a normal private IPv4 address (not link-local) with a
    // usable subnet mask whose prefix length is between 1 and 30 (so /31 and /32 links are skipped).
    private static bool TryCreateAdapter(NetworkInterface networkInterface, out ActiveAdapter adapter)
    {
        var properties = networkInterface.GetIPProperties();
        var unicast = properties.UnicastAddresses
            .FirstOrDefault(candidate =>
                candidate.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate.Address)
                && !IsLinkLocal(candidate.Address)
                && IsPrivateIPv4(candidate.Address));

        if (unicast is null)
        {
            adapter = default!;
            return false;
        }

        var prefixLength = unicast.PrefixLength;

        if (prefixLength <= 0 || prefixLength > 30)
        {
            adapter = default!;
            return false;
        }

        var gateways = properties.GatewayAddresses
            .Select(candidate => candidate.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();
        var dnsServers = properties.DnsAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();
        var networkAddress = FromUInt32(ToUInt32(unicast.Address) & (uint.MaxValue << (32 - prefixLength)));

        adapter = new ActiveAdapter(
            networkInterface.Name.Trim(),
            networkInterface.NetworkInterfaceType.ToString(),
            unicast.Address,
            prefixLength,
            networkAddress,
            gateways,
            dnsServers,
            networkInterface.Speed,
            networkInterface.Description.Trim());
        return true;
    }

    // Builds the list of addresses to ping. If two adapters can reach the same address, it is listed once with both adapter names.
    private static IReadOnlyList<ScanCandidate> BuildScanCandidates(IReadOnlyList<ActiveAdapter> adapters)
    {
        var candidates = new Dictionary<string, ScanCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            foreach (var candidateAddress in EnumerateCandidatesForAdapter(adapter))
            {
                var key = candidateAddress.ToString();

                if (candidates.TryGetValue(key, out var existing))
                {
                    candidates[key] = existing with
                    {
                        AdapterNames = existing.AdapterNames
                            .Concat([adapter.Name])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        IsGateway = existing.IsGateway || adapter.Gateways.Any(gateway => gateway.Equals(candidateAddress))
                    };
                    continue;
                }

                candidates[key] = new ScanCandidate(
                    candidateAddress,
                    [adapter.Name],
                    adapter.Gateways.Any(gateway => gateway.Equals(candidateAddress)));
            }
        }

        return candidates.Values.ToArray();
    }

    // The addresses to try for one adapter. For subnets smaller than a /24 it uses every host (at most 254). For bigger
    // subnets it only samples the /24 around this PC and around each gateway, because scanning thousands of addresses would be slow.
    private static IEnumerable<IPAddress> EnumerateCandidatesForAdapter(ActiveAdapter adapter)
    {
        var addresses = new HashSet<IPAddress>();
        var scanUsesWindow = adapter.PrefixLength < 24;

        if (scanUsesWindow)
        {
            foreach (var candidate in EnumerateLocalWindow(adapter.Address))
            {
                addresses.Add(candidate);
            }

            foreach (var gateway in adapter.Gateways)
            {
                foreach (var candidate in EnumerateLocalWindow(gateway))
                {
                    addresses.Add(candidate);
                }
            }
        }
        else
        {
            var network = ToUInt32(adapter.NetworkAddress);
            // Hosts in a subnet = 2^(host bits) minus the network and broadcast addresses; capped at 254.
            var hostCount = Math.Min((1u << (32 - adapter.PrefixLength)) - 2u, 254u);

            for (var offset = 1u; offset <= hostCount; offset++)
            {
                addresses.Add(FromUInt32(network + offset));
            }
        }

        addresses.Remove(adapter.Address);

        foreach (var gateway in adapter.Gateways)
        {
            addresses.Add(gateway);
        }

        return addresses;
    }

    // Yields .1 to .254 in the same /24 as the given address (the first three numbers stay the same).
    private static IEnumerable<IPAddress> EnumerateLocalWindow(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        if (bytes.Length != 4)
        {
            yield break;
        }

        for (var host = 1; host <= 254; host++)
        {
            yield return new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]);
        }
    }

    // Pings all candidates (at most MaxConcurrentPings at once), then for each device that answered looks up its host name.
    private static async Task<IReadOnlyList<DiscoveredDevice>> ProbeCandidatesAsync(
        IReadOnlyList<ScanCandidate> candidates,
        IReadOnlyList<ActiveAdapter> adapters,
        CancellationToken cancellationToken)
    {
        // The semaphore is a turnstile that lets only 48 pings run at once.
        var semaphore = new SemaphoreSlim(MaxConcurrentPings, MaxConcurrentPings);
        var results = new ConcurrentBag<PingResult>();
        var pingTasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var probe = await PingAsync(candidate.Address, cancellationToken);

                if (probe is not null)
                {
                    results.Add(new PingResult(candidate, probe.Value));
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(pingTasks);
        var responsive = results
            .OrderByDescending(result => result.Candidate.IsGateway)
            .ThenBy(result => result.RoundtripMilliseconds)
            .ToArray();
        var devices = new List<DiscoveredDevice>(responsive.Length);

        foreach (var result in responsive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hostName = await TryResolveHostNameAsync(result.Candidate.Address, cancellationToken);
            var adaptersForDevice = result.Candidate.AdapterNames
                .Select(name => adapters.FirstOrDefault(adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Where(adapter => adapter is not null)
                .Cast<ActiveAdapter>()
                .ToArray();

            devices.Add(new DiscoveredDevice(
                result.Candidate.Address,
                hostName,
                result.RoundtripMilliseconds,
                result.Candidate.IsGateway,
                adaptersForDevice.Select(adapter => adapter.Name).ToArray()));
        }

        return devices;
    }

    // Reports on a single device given by IP address or name: reachable, latency and reverse DNS.
    private static async Task<string> InspectTargetAsync(string target, CancellationToken cancellationToken)
    {
        var resolved = await ResolveTargetAsync(target, cancellationToken);

        if (resolved is null)
        {
            return $"No IPv4 network target could be resolved for \"{target}\".";
        }

        var ping = await PingAsync(resolved.Address, cancellationToken);
        var reverseDns = string.IsNullOrWhiteSpace(resolved.HostName)
            ? await TryResolveHostNameAsync(resolved.Address, cancellationToken)
            : resolved.HostName;
        var lines = new List<string>
        {
            $"Network health for {target.Trim()}:",
            $"- Address: {resolved.Address}",
            $"- Reachable: {(ping is null ? "no response" : "yes")}",
            $"- Latency: {(ping is null ? "n/a" : ping.Value + " ms")}",
            $"- Reverse DNS: {(string.IsNullOrWhiteSpace(reverseDns) ? "none" : reverseDns)}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    // Accepts an IPv4 address as is, or looks a name up in DNS and takes its first IPv4 address; null if neither works.
    private static async Task<ResolvedTarget?> ResolveTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target.Trim(), out var ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            return new ResolvedTarget(ipAddress, string.Empty);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Trim(), cancellationToken);
            var ipv4 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            return ipv4 is null ? null : new ResolvedTarget(ipv4, target.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // Pings once. Returns the round-trip time in milliseconds, or null if there was no reply or an error.
    private static async Task<long?> PingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, PingTimeoutMilliseconds);

            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    // Reverse DNS lookup (address to name), limited to 250 ms; returns empty text on failure or timeout.
    private static async Task<string> TryResolveHostNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var lookupTask = Dns.GetHostEntryAsync(address);
            var completedTask = await Task.WhenAny(lookupTask, Task.Delay(250, cancellationToken));

            if (completedTask != lookupTask)
            {
                return string.Empty;
            }

            var entry = await lookupTask;
            return entry.HostName?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Formats the sweep as text: one line per adapter (with a healthy / degraded / limited verdict), then the devices found.
    private static string RenderSnapshot(NetworkSnapshot snapshot, NetworkRequestMode mode)
    {
        var lines = new List<string>
        {
            mode == NetworkRequestMode.Health ? "Local network health:" : "Local network overview:"
        };

        foreach (var adapter in snapshot.Adapters)
        {
            var gatewayText = adapter.Gateways.Length == 0
                ? "no gateway"
                : string.Join(" | ", adapter.Gateways.Select(gateway =>
                    $"{gateway}{(adapter.GatewayResponded ? " reachable" : " no-response")}"));
            var dnsText = adapter.DnsServers.Length == 0
                ? "no DNS"
                : string.Join(" | ", adapter.DnsServers.Select(address => address.ToString()));
            // healthy = the router answered, degraded = there is a router but it did not answer, limited = no router configured.
            var health = adapter.HasGateway && adapter.GatewayResponded ? "healthy" : adapter.HasGateway ? "degraded" : "limited";

            lines.Add(
                $"- {adapter.Name} [{adapter.Type}] | {adapter.Address}/{adapter.PrefixLength} | gateway {gatewayText} | dns {dnsText} | speed {FormatBitsPerSecond(adapter.SpeedBitsPerSecond)} | {health}");
        }

        lines.Add($"- Scan scope: {snapshot.CandidateCount} host target(s) across {snapshot.Adapters.Length} adapter(s)");
        lines.Add($"- Responding devices: {snapshot.Devices.Length} | scan time {snapshot.Elapsed.TotalSeconds:0.0}s");

        if (snapshot.Devices.Length == 0)
        {
            lines.Add("No local devices responded to the current sweep.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("Discovered devices:");

        // How many devices to list before summarizing the rest as "additional device(s) omitted".
        var maxDevices = mode == NetworkRequestMode.Health ? 16 : 12;

        foreach (var device in snapshot.Devices.Take(maxDevices))
        {
            var deviceParts = new List<string>
            {
                $"- {device.Address}"
            };

            if (!string.IsNullOrWhiteSpace(device.HostName))
            {
                deviceParts.Add(device.HostName);
            }

            if (device.IsGateway)
            {
                deviceParts.Add("gateway");
            }

            if (device.RoundtripMilliseconds is not null)
            {
                deviceParts.Add(device.RoundtripMilliseconds.Value + " ms");
            }

            if (device.AdapterNames.Length > 0)
            {
                deviceParts.Add("via " + string.Join("/", device.AdapterNames));
            }

            lines.Add(string.Join(" | ", deviceParts));
        }

        if (snapshot.Devices.Length > maxDevices)
        {
            lines.Add($"- {snapshot.Devices.Length - maxDevices} additional device(s) omitted from the preview.");
        }

        if (snapshot.Adapters.Any(adapter => adapter.PrefixLength < 24))
        {
            lines.Add("Large private subnets are sampled within the local /24 neighborhood to keep discovery responsive.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // True for the private ranges 10.x.x.x, 172.16-31.x.x and 192.168.x.x.
    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            10 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    // True for 169.254.x.x, the address Windows gives itself when no DHCP server answered.
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    // Converts an IPv4 address to a single number (and FromUInt32 converts back) so addresses can be counted by adding to them.
    private static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    // Link speed as text, in steps of 1000: "867 Mbps", "1 Gbps". Zero or negative gives "unknown".
    private static string FormatBitsPerSecond(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "unknown";
        }

        double value = bitsPerSecond;
        string[] units = { "bps", "Kbps", "Mbps", "Gbps" };
        var unitIndex = 0;

        while (value >= 1000 && unitIndex < units.Length - 1)
        {
            value /= 1000;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private sealed record NetworkRequest(NetworkRequestMode Mode, string Target);

    // What kind of report was asked for: Overview (default), Health (same sweep, health wording) or Inspect (one device).
    internal enum NetworkRequestMode
    {
        Overview,
        Health,
        Inspect
    }

    // One usable network adapter with its address, subnet size, gateways (routers), DNS servers and link speed.
    private sealed record ActiveAdapter(
        string Name,
        string Type,
        IPAddress Address,
        int PrefixLength,
        IPAddress NetworkAddress,
        IPAddress[] Gateways,
        IPAddress[] DnsServers,
        long SpeedBitsPerSecond,
        string Description)
    {
        public bool HasGateway => Gateways.Length > 0;

        public bool GatewayResponded { get; set; }
    }

    // An address that will be pinged, and which adapters can reach it.
    private sealed record ScanCandidate(IPAddress Address, string[] AdapterNames, bool IsGateway);

    // A candidate that answered a ping, with its round-trip time.
    private sealed record PingResult(ScanCandidate Candidate, long RoundtripMilliseconds);

    // Everything one sweep found: adapters, responding devices, how many addresses were tried and how long it took.
    private sealed record NetworkSnapshot(
        ActiveAdapter[] Adapters,
        DiscoveredDevice[] Devices,
        int CandidateCount,
        TimeSpan Elapsed);

    // One device that answered, with what is known about it.
    private sealed record DiscoveredDevice(
        IPAddress Address,
        string HostName,
        long? RoundtripMilliseconds,
        bool IsGateway,
        string[] AdapterNames);

    // The address (and name, if one was typed) of a device to inspect.
    private sealed record ResolvedTarget(IPAddress Address, string HostName);
}
