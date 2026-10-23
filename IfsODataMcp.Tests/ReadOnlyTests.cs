using System.Net;
using System.Text.RegularExpressions;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Gateway;
using IfsODataMcp.Core.Http;
using IfsODataMcp.Core.Query;
using MockODataService;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IfsODataMcp.Tests;

/// <summary>Evidence that no write request can leave this process towards the target API.</summary>
public class ReadOnlyTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Uri Origin = new("https://erp.example.test");

    private static HttpClient GuardedClient(StubHandler inner, bool allowInsecure = false) =>
        new(new ReadOnlyGuardHandler(Origin, allowInsecure) { InnerHandler = inner });

    public static TheoryData<string> NonGetMethods() => new() { "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "MERGE" };

    [Theory]
    [MemberData(nameof(NonGetMethods))]
    public async Task Guard_rejects_every_non_get_method_before_it_reaches_the_network(string method)
    {
        var inner = StubHandler.Json("{}");
        using var client = GuardedClient(inner);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Origin, "/odata/X/Y"));

        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => client.SendAsync(request));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Guard_rejects_a_get_with_a_body()
    {
        var inner = StubHandler.Json("{}");
        using var client = GuardedClient(inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin, "/x")) { Content = new StringContent("x") };

        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => client.SendAsync(request));
        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData("https://other.example.test/x")]
    [InlineData("https://erp.example.test:8443/x")]
    [InlineData("http://erp.example.test/x")]
    public async Task Guard_rejects_requests_to_other_origins(string url)
    {
        var inner = StubHandler.Json("{}");
        using var client = GuardedClient(inner);

        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => client.GetAsync(url));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Guard_allows_a_plain_get_to_the_configured_origin()
    {
        var inner = StubHandler.Json("{}");
        using var client = GuardedClient(inner);

        using var response = await client.GetAsync(new Uri(Origin, "/odata/X/Y"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Get, Assert.Single(inner.Requests).Method);
    }

    [Fact]
    public async Task Guard_refuses_plain_http_to_a_non_loopback_host_unless_allowed()
    {
        var inner = StubHandler.Json("{}");
        var plain = new Uri("http://erp.example.test");
        using var strict = new HttpClient(new ReadOnlyGuardHandler(plain, false) { InnerHandler = inner });
        using var relaxed = new HttpClient(new ReadOnlyGuardHandler(plain, true) { InnerHandler = inner });

        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => strict.GetAsync("http://erp.example.test/x"));
        using var ok = await relaxed.GetAsync("http://erp.example.test/x");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Every_gateway_operation_sends_only_get_requests()
    {
        // Run all operations against the real mock service through a recording handler.
        var recorder = new RecordingHandler(factory.Server.CreateHandler());
        var gateway = TestSupport.Gateway(recorder);
        var keys = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["SupplierId"] = System.Text.Json.JsonDocument.Parse("\"S-1001\"").RootElement.Clone(),
        };

        await gateway.ListProjectionsAsync(true, CancellationToken.None);
        await gateway.DescribeEntitySetAsync("ProcurementDemo", "Suppliers", CancellationToken.None);
        await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(Top: 3, Expand: ["PurchaseOrders"]), CancellationToken.None);
        await gateway.GetEntityAsync("ProcurementDemo", "Suppliers", keys, new ODataQueryRequest(), CancellationToken.None);
        await gateway.BuildUrlAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), null, CancellationToken.None);

        Assert.NotEmpty(recorder.Methods);
        Assert.All(recorder.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public void Source_code_contains_no_write_verbs_for_the_target_api()
    {
        // Static backstop: the projects that talk to the target API must not even mention a write path.
        var root = FindRepositoryRoot();
        var forbidden = new Regex(
            @"HttpMethod\.(Post|Put|Patch|Delete|Head|Options|Trace|Connect)|new\s+HttpMethod\s*\(|\.(PostAsync|PutAsync|PatchAsync|DeleteAsync|PostAsJsonAsync|PutAsJsonAsync|PatchAsJsonAsync)\s*\(",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var project in new[] { "IfsODataMcp.Core", "IfsODataMcp.Server" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (forbidden.IsMatch(File.ReadAllText(file))) offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_http_client_wrapper_exposes_no_way_to_choose_a_method()
    {
        var publicMethods = typeof(ODataHttpClient).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.Equal(["GetStringAsync"], publicMethods.Select(m => m.Name).ToArray());
        Assert.DoesNotContain(publicMethods.SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(HttpMethod));
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IfsAgentBridge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly List<HttpMethod> _methods = [];

        public IReadOnlyList<HttpMethod> Methods => _methods;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_methods) _methods.Add(request.Method);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
