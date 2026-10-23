using System.Net;
using System.Text;
using IfsODataMcp.Core.Audit;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Gateway;
using IfsODataMcp.Core.Http;
using IfsODataMcp.Core.Metadata;
using IfsODataMcp.Core.Policy;
using IfsODataMcp.Core.Query;
using IfsODataMcp.Core.Security;
using MockODataService;

namespace IfsODataMcp.Tests;

/// <summary>Shared builders so each test states only what it cares about.</summary>
internal static class TestSupport
{
    public const string ProjectionName = "ProcurementDemo";

    public static ServiceMetadata Metadata() => MetadataParser.Parse(MockData.ProcurementMetadata);

    public static PolicyOptions Options(Action<PolicyOptions>? configure = null)
    {
        var options = new PolicyOptions
        {
            Projections = [new ProjectionRule { Name = ProjectionName }],
            DeniedProperties = ["ContactEmail", "ContactPhone"],
        };
        configure?.Invoke(options);
        return options;
    }

    public static ODataEndpointOptions Endpoint() => new()
    {
        BaseUrl = "http://localhost",
        ServiceRootTemplate = "{baseUrl}/odata/{projection}/",
    };

    /// <summary>Gateway wired to the given handler, with the same pipeline order as the real server.</summary>
    public static ODataGateway Gateway(
        HttpMessageHandler inner, PolicyOptions? options = null, IAuditLog? audit = null, SecretRedactor? redactor = null)
    {
        options ??= Options();
        var endpoint = Endpoint();
        var urls = new ODataUrlBuilder(endpoint);
        var guard = new ReadOnlyGuardHandler(urls.BaseUri, endpoint.AllowInsecureHttp) { InnerHandler = inner };
        var http = new ODataHttpClient(new HttpClient(guard), options, redactor ?? new SecretRedactor());
        return new ODataGateway(new AccessPolicy(options), urls, http, audit ?? new NullAuditLog());
    }

    public static ValidatedQuery Validate(ODataQueryRequest request, PolicyOptions? options = null)
    {
        var policy = new AccessPolicy(options ?? Options());
        var metadata = Metadata();
        var rule = policy.EnsureProjectionAllowed(ProjectionName);
        return new QueryValidator(policy).Validate(rule, metadata, metadata.FindEntitySet("PurchaseOrders")!, request);
    }
}

/// <summary>Records every request and answers with a canned response.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri!));
        return Task.FromResult(respond(request));
    }

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static StubHandler Xml(string xml, Func<HttpRequestMessage, HttpResponseMessage> otherwise) =>
        new(request => request.RequestUri!.AbsolutePath.EndsWith("$metadata", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }
            : otherwise(request));
}

internal sealed class MemoryAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public void Write(AuditEntry entry) => Entries.Add(entry);
}
