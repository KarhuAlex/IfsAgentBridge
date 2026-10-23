using IfsODataMcp.Core;
using IfsODataMcp.Core.Security;

namespace IfsODataMcp.Auth;

public enum AuthMode
{
    /// <summary>No credentials. For the mock service only.</summary>
    None,

    /// <summary>A token that is already available in an environment variable. For development.</summary>
    StaticBearer,

    /// <summary>OpenID Connect authorization code flow with PKCE and a localhost callback.</summary>
    InteractivePkce,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.None;

    public string StaticBearerEnvVar { get; set; } = "IFS_BRIDGE_TOKEN";

    public bool LoginAtStartup { get; set; } = true;

    public OidcOptions Oidc { get; set; } = new();
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Space-separated scopes. Add <c>offline_access</c> if the provider should issue a refresh token.</summary>
    public string Scope { get; set; } = "openid";

    /// <summary>Loopback port for the redirect URI. 0 picks a free port (the provider must then accept any loopback port).</summary>
    public int RedirectPort { get; set; }

    public string RedirectPath { get; set; } = "/callback";

    public int LoginTimeoutSeconds { get; set; } = 120;

    public int ExpirySkewSeconds { get; set; } = 60;

    public bool AllowInsecureAuthority { get; set; }
}

public interface IAuthProvider
{
    string Name { get; }

    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Called when the API rejected the token, so the next call acquires a fresh one.</summary>
    void Invalidate();
}

public sealed class NoAuthProvider : IAuthProvider
{
    public string Name => "none";

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public void Invalidate()
    {
    }
}

public sealed class StaticBearerProvider : IAuthProvider
{
    private readonly string _token;

    public StaticBearerProvider(string environmentVariable, SecretRedactor redactor, Func<string, string?>? readEnvironment = null)
    {
        var token = (readEnvironment ?? Environment.GetEnvironmentVariable).Invoke(environmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            // Only the variable's name is mentioned, never a value.
            throw new ConfigurationException($"Auth mode StaticBearer needs a token in the environment variable '{environmentVariable}'.");
        }

        _token = token.Trim();
        redactor.Register(_token);
    }

    public string Name => "static-bearer";

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(_token);

    public void Invalidate()
    {
        // A static token cannot be refreshed; a 401 simply surfaces to the caller.
    }
}
