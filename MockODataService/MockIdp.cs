using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace MockODataService;

/// <summary>Mock OpenID Connect provider for demos and tests. Approves every request.</summary>
public sealed class MockIdp
{
    public const string ClientId = "ifs-agent-bridge-demo";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _accessTokens = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
    private int _authorizationGrants;
    private int _refreshGrants;
    private int _lifetimeSeconds = 3600;

    public int AuthorizationGrants => _authorizationGrants;

    public int RefreshGrants => _refreshGrants;

    public void Map(WebApplication app)
    {
        if (app.Configuration.GetValue<int?>("Mock:TokenLifetimeSeconds") is { } lifetime)
        {
            _lifetimeSeconds = lifetime;
        }

        app.MapGet("/idp/.well-known/openid-configuration", (HttpContext c) =>
        {
            var issuer = Issuer(c.Request);
            return Results.Json(new
            {
                issuer,
                authorization_endpoint = issuer + "/authorize",
                token_endpoint = issuer + "/token",
                jwks_uri = issuer + "/jwks",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                code_challenge_methods_supported = new[] { "S256" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "none" },
            });
        });

        app.MapGet("/idp/jwks", () =>
        {
            var p = _key.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = _keyId, n = B64(p.Modulus!), e = B64(p.Exponent!) },
                },
            });
        });

        app.MapGet("/idp/authorize", (HttpContext c) =>
        {
            var q = c.Request.Query;
            if (q["client_id"] != ClientId || q["response_type"] != "code" || q["code_challenge_method"] != "S256"
                || string.IsNullOrEmpty(q["code_challenge"]) || !Uri.TryCreate(q["redirect_uri"], UriKind.Absolute, out var redirect)
                || !redirect.IsLoopback)
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }

            var code = Random();
            _codes[code] = new PendingCode(q["code_challenge"]!, q["nonce"].ToString(), q["redirect_uri"]!, q["scope"].ToString());
            var separator = redirect.Query.Length > 0 ? "&" : "?";
            return Results.Redirect($"{redirect.AbsoluteUri}{separator}code={code}&state={Uri.EscapeDataString(q["state"].ToString())}");
        });

        app.MapPost("/idp/token", async (HttpContext c) =>
        {
            var form = await c.Request.ReadFormAsync();
            if (form["client_id"] != ClientId) return Results.Json(new { error = "invalid_client" }, statusCode: 400);

            switch (form["grant_type"].ToString())
            {
                case "authorization_code":
                {
                    if (!_codes.TryRemove(form["code"].ToString(), out var pending)
                        || pending.RedirectUri != form["redirect_uri"]
                        || B64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != pending.Challenge)
                    {
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    }

                    Interlocked.Increment(ref _authorizationGrants);
                    return Results.Json(IssueTokens(c.Request, pending.Nonce, pending.Scope));
                }
                case "refresh_token":
                {
                    if (!_refreshTokens.TryRemove(form["refresh_token"].ToString(), out var scope))
                    {
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    }

                    Interlocked.Increment(ref _refreshGrants);
                    return Results.Json(IssueTokens(c.Request, null, scope));
                }
                default:
                    return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
            }
        });
    }

    public bool IsAuthorized(StringValues authorization, string? staticToken)
    {
        var header = authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;

        var token = header["Bearer ".Length..];
        return (staticToken is not null && token == staticToken)
               || (_accessTokens.TryGetValue(token, out var expires) && expires > DateTimeOffset.UtcNow);
    }

    private object IssueTokens(HttpRequest request, string? nonce, string scope)
    {
        var access = "mock-at-" + Random();
        var refresh = "mock-rt-" + Random();
        var expires = DateTimeOffset.UtcNow.AddSeconds(_lifetimeSeconds);
        _accessTokens[access] = expires;
        _refreshTokens[refresh] = scope;

        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = Issuer(request),
            ["sub"] = "demo-user",
            ["aud"] = ClientId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(Math.Max(_lifetimeSeconds, 60)).ToUnixTimeSeconds(),
            ["at_hash"] = B64(SHA256.HashData(Encoding.ASCII.GetBytes(access))[..16]),
        };
        if (!string.IsNullOrEmpty(nonce)) claims["nonce"] = nonce;

        return new
        {
            access_token = access,
            token_type = "Bearer",
            expires_in = _lifetimeSeconds,
            refresh_token = refresh,
            id_token = Sign(claims),
            scope,
        };
    }

    private string Sign(Dictionary<string, object?> claims)
    {
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = _keyId }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = _key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(signature)}";
    }

    private static string Issuer(HttpRequest request) => $"{request.Scheme}://{request.Host}/idp";

    private static string Random() => B64(RandomNumberGenerator.GetBytes(24));

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PendingCode(string Challenge, string Nonce, string RedirectUri, string Scope);
}
