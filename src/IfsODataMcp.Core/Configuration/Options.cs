namespace IfsODataMcp.Core.Configuration;

/// <summary>Where the target OData API lives and how its URLs are shaped. Nothing here is hard-coded.</summary>
public sealed class ODataEndpointOptions
{
    /// <summary>Origin of the target API, for example <c>https://erp.example.com</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// URL of a projection's service root. Placeholders: <c>{baseUrl}</c>, <c>{projection}</c>.
    /// This is an assumption about the target product and must be set explicitly.
    /// </summary>
    public string ServiceRootTemplate { get; set; } = "";

    /// <summary>Path of the metadata document, relative to the service root.</summary>
    public string MetadataPath { get; set; } = "$metadata";

    /// <summary>Allows plain http to a non-loopback host. Off by default so tokens never travel unencrypted.</summary>
    public bool AllowInsecureHttp { get; set; }
}

public sealed class ProjectionRule
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Optional entity-set allow-list. Null or empty means every entity set of the projection.</summary>
    public List<string>? EntitySets { get; set; }
}

/// <summary>Allow-lists, deny-lists and limits. Defaults are deliberately restrictive.</summary>
public sealed class PolicyOptions
{
    /// <summary>Projections the agent may use. Empty means nothing is reachable.</summary>
    public List<ProjectionRule> Projections { get; set; } = [];

    /// <summary>
    /// Properties that are never selected, filtered on, ordered by or returned.
    /// Entry forms: <c>Property</c> (any entity) or <c>Scope.Property</c> where scope is an entity type or entity set name.
    /// </summary>
    public List<string> DeniedProperties { get; set; } = [];

    public int DefaultTop { get; set; } = 50;

    public int MaxTop { get; set; } = 200;

    public int MaxExpandDepth { get; set; } = 1;

    public int MaxFilterLength { get; set; } = 2000;

    public int MaxResponseBytes { get; set; } = 1_000_000;

    public int RequestTimeoutSeconds { get; set; } = 30;
}

public sealed class AuditOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>JSON-lines file. Empty means a file under the user's local application data folder.</summary>
    public string? Path { get; set; }
}
