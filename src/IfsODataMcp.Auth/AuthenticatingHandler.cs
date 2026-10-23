using System.Net;
using System.Net.Http.Headers;

namespace IfsODataMcp.Auth;

/// <summary>Adds the bearer token to outgoing requests. Sits inside the read-only guard, so only GETs ever get here.</summary>
public sealed class AuthenticatingHandler(IAuthProvider auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            auth.Invalidate();
        }

        return response;
    }
}
