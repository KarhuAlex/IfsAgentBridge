namespace IfsODataMcp.Core.Configuration;

public sealed class ODataEndpointOptions
{
    /// <summary>Origin of the target API, for example <c>https://erp.example.com</c>.</summary>
    public string BaseUrl { get; set; } = "";

    public string ServiceRootTemplate { get; set; } = "";

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

public sealed class PolicyOptions
{
    /// <summary>Projections the agent may use. Empty means nothing is reachable.</summary>
    public List<ProjectionRule> Projections { get; set; } = [];

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
