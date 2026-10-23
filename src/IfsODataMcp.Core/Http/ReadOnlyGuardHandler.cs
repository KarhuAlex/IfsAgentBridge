using System.Net;

namespace IfsODataMcp.Core.Http;

/// <summary>
/// Outermost handler of the target-API pipeline. It is the last line of defence behind the code paths that only
/// ever construct GET requests: anything that is not a GET, or that points away from the configured origin,
/// is rejected before authentication is added or a socket is opened.
/// </summary>
public sealed class ReadOnlyGuardHandler(Uri allowedBase, bool allowInsecureHttp) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
        {
            throw new ReadOnlyViolationException($"Blocked {request.Method} request: this bridge only sends HTTP GET.");
        }

        var uri = request.RequestUri ?? throw new ReadOnlyViolationException("Blocked request without a URI.");
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, allowedBase.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, allowedBase.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != allowedBase.Port)
        {
            throw new ReadOnlyViolationException("Blocked request to a host other than the configured API origin.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp && !IsLoopback(uri))
        {
            throw new ReadOnlyViolationException("Blocked plain-http request to a non-loopback host (set Endpoint:AllowInsecureHttp to override).");
        }

        if (request.Content is not null)
        {
            throw new ReadOnlyViolationException("Blocked request with a body.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
}
