using Microsoft.Extensions.Primitives;

namespace MockODataService;

/// <summary>Placeholder; replaced by a full mock identity provider when the auth providers are built.</summary>
public sealed class MockIdp
{
    public void Map(WebApplication app)
    {
    }

    public bool IsAuthorized(StringValues authorization, string? staticToken) =>
        staticToken is not null && authorization.ToString() == "Bearer " + staticToken;
}
