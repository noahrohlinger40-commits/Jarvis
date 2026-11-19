// LocalAiPolicy.cs: the "local AI only" rule. When the LocalAiOnly setting is on, Jarvis may only send model
// requests to a server on this same computer. ModelGateway.cs asks AllowsModelProvider when building providers
// and builds its HTTP client with CreateHttpClient so a request can never be redirected off the machine.
using System.Net;

namespace Jarvis.Core;

/// <summary>Enforces same-computer HTTP inference; this is not an OS-wide network firewall.</summary>
public static class LocalAiPolicy
{
    // True only for an http/https URL with no embedded user:password whose host is localhost or a loopback IP
    // (127.0.0.1, ::1). Example: "http://localhost:8179/v1" passes, "http://192.168.1.5:8179" does not.
    public static bool IsLoopbackHttpEndpoint(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    // When local-only mode is off everything is allowed; when on, only provider "local" at a loopback address.
    public static bool AllowsModelProvider(JarvisOptions options, string provider, string address) =>
        !options.LocalAiOnly || (string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase)
            && IsLoopbackHttpEndpoint(address));

    // Throws if the address is not a loopback endpoint; otherwise returns it (with "localhost" rewritten to 127.0.0.1).
    public static Uri RequireLocalEndpoint(Uri address)
    {
        if (!IsLoopbackHttpEndpoint(address.AbsoluteUri))
            throw new InvalidOperationException("Local-only AI mode blocked an endpoint outside this computer.");

        // Resolve the special localhost name here so no hosts-file or DNS mapping can redirect inference.
        return string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(address) { Host = "127.0.0.1" }.Uri : address;
    }

    // In local-only mode the client follows no redirects, ignores system proxy settings, and checks every request's
    // address (LocalRequestHandler). Otherwise it is an ordinary HttpClient.
    public static HttpClient CreateHttpClient(bool localAiOnly, TimeSpan timeout)
    {
        var transport = new HttpClientHandler
        {
            AllowAutoRedirect = !localAiOnly,
            UseProxy = !localAiOnly
        };
        HttpMessageHandler handler = localAiOnly ? new LocalRequestHandler(transport) : transport;
        return new HttpClient(handler) { Timeout = timeout };
    }

    // Sits in front of the real HTTP handler and rejects any request that is not aimed at this computer.
    private sealed class LocalRequestHandler(HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = RequireLocalEndpoint(request.RequestUri
                ?? throw new InvalidOperationException("An AI request has no endpoint."));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
