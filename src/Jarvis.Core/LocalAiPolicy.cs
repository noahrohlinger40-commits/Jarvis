using System.Net;

namespace Jarvis.Core;

/// <summary>Enforces same-computer HTTP inference; this is not an OS-wide network firewall.</summary>
public static class LocalAiPolicy
{
    public static bool IsLoopbackHttpEndpoint(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    public static bool AllowsModelProvider(JarvisOptions options, string provider, string address) =>
        !options.LocalAiOnly || (string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase)
            && IsLoopbackHttpEndpoint(address));

    public static Uri RequireLocalEndpoint(Uri address)
    {
        if (!IsLoopbackHttpEndpoint(address.AbsoluteUri))
            throw new InvalidOperationException("Local-only AI mode blocked an endpoint outside this computer.");

        // Resolve the special localhost name here so no hosts-file or DNS mapping can redirect inference.
        return string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(address) { Host = "127.0.0.1" }.Uri : address;
    }

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
