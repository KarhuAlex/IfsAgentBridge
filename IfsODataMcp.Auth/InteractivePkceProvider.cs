using System.Net;
using System.Net.Sockets;
using IdentityModel.OidcClient;
using IdentityModel.OidcClient.Browser;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Security;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Auth;

/// <summary>
/// OpenID Connect authorization code flow with PKCE. The system browser performs the sign-in, a loopback listener
/// receives the redirect, and the resulting tokens live in memory only: they are never written to disk or logged.
/// </summary>
public sealed class InteractivePkceProvider : IAuthProvider, IDisposable
{
    private readonly OidcOptions _options;
    private readonly SecretRedactor _redactor;
    private readonly ILogger _logger;
    private readonly Func<int, string, IBrowser> _browserFactory;
    private readonly HttpMessageHandler? _backchannel;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OidcClient? _client;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public InteractivePkceProvider(
        OidcOptions options,
        SecretRedactor redactor,
        ILogger logger,
        Func<int, string, IBrowser>? browserFactory = null,
        HttpMessageHandler? backchannel = null,
        TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new ConfigurationException("Auth:Oidc:Authority and Auth:Oidc:ClientId are required for InteractivePkce.");
        }

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttps && !authority.IsLoopback && !options.AllowInsecureAuthority))
        {
            throw new ConfigurationException("Auth:Oidc:Authority must be an https URL (http is accepted for loopback only).");
        }

        _options = options;
        _redactor = redactor;
        _logger = logger;
        _browserFactory = browserFactory ?? ((port, path) => new LoopbackBrowser(port, path));
        _backchannel = backchannel;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "interactive-pkce";

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached))
            {
                return cached;
            }

            if (_refreshToken is not null && await TryRefreshAsync(cancellationToken).ConfigureAwait(false))
            {
                return _accessToken;
            }

            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        // Keep the refresh token: the access token was rejected, but a refresh may still succeed.
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    public void Dispose() => _gate.Dispose();

    private bool TryGetCached(out string? token)
    {
        token = _accessToken;
        return token is not null && _time.GetUtcNow() < _expiresAt - TimeSpan.FromSeconds(_options.ExpirySkewSeconds);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        var port = _options.RedirectPort != 0 ? _options.RedirectPort : FindFreePort();
        var path = _options.RedirectPath.StartsWith('/') ? _options.RedirectPath : "/" + _options.RedirectPath;

        _client = new OidcClient(new OidcClientOptions
        {
            Authority = _options.Authority,
            ClientId = _options.ClientId,
            Scope = _options.Scope,
            RedirectUri = $"http://127.0.0.1:{port}{path}",
            Browser = _browserFactory(port, path),
            BackchannelHandler = _backchannel,
            LoadProfile = false,
            // The ID token signature is always verified against the provider's published keys.
            IdentityTokenValidator = new JwtHandlerIdentityTokenValidator(),
        });

        _logger.LogInformation("Opening the system browser for sign-in (waiting up to {Seconds}s).", _options.LoginTimeoutSeconds);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.LoginTimeoutSeconds));

        LoginResult result;
        try
        {
            result = await _client.LoginAsync(new LoginRequest(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationFailedException("Sign-in was not completed in time.");
        }

        if (result.IsError)
        {
            throw new AuthenticationFailedException(
                $"Sign-in failed: {_redactor.Redact(result.Error)} {_redactor.Redact(result.ErrorDescription)}".TrimEnd());
        }

        Store(result.AccessToken, result.RefreshToken, result.IdentityToken, result.AccessTokenExpiration);
        _logger.LogInformation("Signed in. The token is kept in memory only.");
    }

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _refreshToken is null)
        {
            return false;
        }

        try
        {
            var result = await _client.RefreshTokenAsync(_refreshToken, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.IsError)
            {
                _logger.LogInformation("Token refresh was refused; a new sign-in is needed.");
                _refreshToken = null;
                return false;
            }

            Store(result.AccessToken, result.RefreshToken ?? _refreshToken, result.IdentityToken, result.AccessTokenExpiration);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation("Token refresh failed ({Type}); a new sign-in is needed.", ex.GetType().Name);
            _refreshToken = null;
            return false;
        }
    }

    private void Store(string? accessToken, string? refreshToken, string? identityToken, DateTimeOffset expiration)
    {
        _redactor.Register(accessToken);
        _redactor.Register(refreshToken);
        _redactor.Register(identityToken);

        _accessToken = accessToken ?? throw new AuthenticationFailedException("The identity provider returned no access token.");
        _refreshToken = refreshToken;
        _expiresAt = expiration;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
