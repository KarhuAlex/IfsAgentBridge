using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Audit;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Gateway;
using IfsODataMcp.Core.Query;
using Microsoft.AspNetCore.Mvc.Testing;
using MockODataService;

namespace IfsODataMcp.Tests;

public class GatewayTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private ODataGateway Gateway(Action<PolicyOptions>? configure = null, IAuditLog? audit = null) =>
        TestSupport.Gateway(factory.Server.CreateHandler(), TestSupport.Options(configure), audit);

    private static Dictionary<string, JsonElement> Keys(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task List_projections_returns_only_the_allow_list()
    {
        var projections = await Gateway().ListProjectionsAsync(false, Ct);

        Assert.Equal(["ProcurementDemo"], projections.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task List_projections_can_include_allowed_entity_sets()
    {
        var projections = await Gateway(o => o.Projections[0].EntitySets = ["Suppliers", "PurchaseOrders"]).ListProjectionsAsync(true, Ct);

        Assert.Equal(["PurchaseOrders", "Suppliers"], projections.Single().EntitySets);
    }

    [Fact]
    public async Task Describe_returns_keys_properties_and_navigation_properties()
    {
        var description = await Gateway().DescribeEntitySetAsync("ProcurementDemo", "OrderLines", Ct);

        Assert.Equal("OrderLine", description.EntityType);
        Assert.Equal(["OrderNo", "LineNo"], description.Keys.Select(k => k.Name).ToArray());
        Assert.Contains(description.Properties, p => p is { Name: "UnitPrice", Type: "Edm.Decimal" });
        var order = Assert.Single(description.NavigationProperties);
        Assert.Equal(("Order", false), (order.Name, order.IsCollection));
        Assert.Equal(["PurchaseOrders"], order.TargetEntitySets);
    }

    [Fact]
    public async Task Describe_hides_denied_properties_and_unreachable_navigations()
    {
        var description = await Gateway(o =>
        {
            o.Projections[0].EntitySets = ["Suppliers"];
        }).DescribeEntitySetAsync("ProcurementDemo", "Suppliers", Ct);

        Assert.DoesNotContain(description.Properties, p => p.Name is "ContactEmail" or "ContactPhone");
        Assert.Empty(description.NavigationProperties);
    }

    [Fact]
    public async Task Describe_rejects_projections_and_entity_sets_outside_policy()
    {
        var gateway = Gateway(o => o.Projections[0].EntitySets = ["Suppliers"]);

        await Assert.ThrowsAsync<PolicyViolationException>(() => gateway.DescribeEntitySetAsync("StaffDirectoryDemo", "Employees", Ct));
        await Assert.ThrowsAsync<PolicyViolationException>(() => gateway.DescribeEntitySetAsync("ProcurementDemo", "PurchaseOrders", Ct));
    }

    [Fact]
    public async Task Describe_reports_unknown_entity_sets()
    {
        await Assert.ThrowsAsync<QueryValidationException>(() => Gateway().DescribeEntitySetAsync("ProcurementDemo", "Nope", Ct));
    }

    [Fact]
    public async Task Query_applies_select_filter_orderby_and_top()
    {
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "PurchaseOrders",
            new ODataQueryRequest(Select: ["OrderNo", "Status"], Filter: "Status eq 'Closed'", OrderBy: "OrderNo desc", Top: 5), Ct);

        var rows = outcome.Data.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(5, outcome.RowCount);
        Assert.All(rows, r => Assert.Equal("Closed", (string?)r["Status"]));
        Assert.All(rows, r => Assert.Equal(["OrderNo", "Status"], r.Select(p => p.Key).ToArray()));
        Assert.Equal(rows.Select(r => (string)r["OrderNo"]!).OrderByDescending(x => x, StringComparer.Ordinal), rows.Select(r => (string)r["OrderNo"]!));
    }

    [Fact]
    public async Task Query_without_top_uses_the_default_of_50()
    {
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "OrderLines", new ODataQueryRequest(), Ct);

        Assert.Equal(50, outcome.RowCount);
        Assert.False(outcome.HasMore == false && outcome.RowCount == 0);
    }

    [Fact]
    public async Task Query_with_top_above_the_cap_is_clamped_and_reports_a_warning()
    {
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "OrderLines", new ODataQueryRequest(Top: 100_000), Ct);

        Assert.True(outcome.RowCount <= 200);
        Assert.Contains(outcome.Warnings, w => w.Contains("200"));
    }

    [Fact]
    public async Task Query_never_returns_denied_properties_even_when_selected_or_expanded()
    {
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "PurchaseOrders",
            new ODataQueryRequest(Select: ["OrderNo"], Expand: ["Supplier"], Top: 3), Ct);

        var json = outcome.Data.ToJsonString();
        Assert.DoesNotContain("ContactEmail", json);
        Assert.DoesNotContain("ContactPhone", json);
        Assert.Contains("\"Supplier\":{", json);
    }

    [Fact]
    public async Task Query_removes_denied_properties_from_select_and_says_so()
    {
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "Suppliers",
            new ODataQueryRequest(Select: ["Name", "ContactEmail"], Top: 2), Ct);

        Assert.DoesNotContain("ContactEmail", outcome.Data.ToJsonString());
        Assert.Contains(outcome.Warnings, w => w.Contains("ContactEmail"));
    }

    [Fact]
    public async Task Query_scrubs_denied_properties_even_if_the_server_ignores_select()
    {
        // A misbehaving server returns everything, including denied fields and a nested related entity.
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"@odata.context":"x","value":[{"@odata.etag":"W/1","SupplierId":"S-1","Name":"A","ContactEmail":"a@example.invalid",
                 "PurchaseOrders":[{"OrderNo":"PO-1","Note":"n"}]}]}
                """, Encoding.UTF8, "application/json"),
        });
        var gateway = TestSupport.Gateway(handler, TestSupport.Options(o => o.DeniedProperties = ["ContactEmail", "PurchaseOrder.Note"]));

        var outcome = await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct);

        var json = outcome.Data.ToJsonString();
        Assert.DoesNotContain("ContactEmail", json);
        Assert.DoesNotContain("\"Note\"", json);
        Assert.DoesNotContain("@odata", json);
        Assert.Contains("PO-1", json);
    }

    [Fact]
    public async Task Query_drops_rows_beyond_top_if_the_server_returns_too_many()
    {
        var rows = string.Join(",", Enumerable.Range(1, 10).Select(i => $$"""{"SupplierId":"S-{{i}}"}"""));
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"value":[{{rows}}]}""", Encoding.UTF8, "application/json"),
        });

        var outcome = await TestSupport.Gateway(handler).QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(Top: 3), Ct);

        Assert.Equal(3, outcome.RowCount);
        Assert.True(outcome.HasMore);
        Assert.Contains(outcome.Warnings, w => w.Contains("extra rows"));
    }

    [Fact]
    public async Task Query_reports_that_more_data_exists_when_the_server_pages()
    {
        var outcome = await Gateway(o => { o.DefaultTop = 150; o.MaxTop = 200; }).QueryAsync("ProcurementDemo", "OrderLines", new ODataQueryRequest(Top: 200), Ct);

        Assert.True(outcome.RowCount > 0);
    }

    [Fact]
    public async Task Query_returns_text_from_the_erp_as_plain_data()
    {
        // The mock contains a note that tries to instruct the agent. It must arrive as an ordinary string value.
        var outcome = await Gateway().QueryAsync("ProcurementDemo", "PurchaseOrders",
            new ODataQueryRequest(Select: ["OrderNo", "Note"], Filter: "contains(Note,'IMPORTANT SYSTEM INSTRUCTION')"), Ct);

        var row = Assert.Single(outcome.Data.AsArray())!.AsObject();
        Assert.Contains("ignore all previous instructions", (string)row["Note"]!);
        Assert.IsAssignableFrom<JsonValue>(row["Note"]);
    }

    [Fact]
    public async Task Query_enforces_policy_before_any_request_is_made()
    {
        var handler = StubHandler.Json("{}");
        var gateway = TestSupport.Gateway(handler);

        await Assert.ThrowsAsync<PolicyViolationException>(() =>
            gateway.QueryAsync("StaffDirectoryDemo", "Employees", new ODataQueryRequest(), Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Metadata_is_fetched_once_and_cached()
    {
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""", Encoding.UTF8, "application/json"),
        });
        var gateway = TestSupport.Gateway(handler);

        await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct);
        await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct);

        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("$metadata", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Get_entity_returns_one_entity_by_composite_key()
    {
        var outcome = await Gateway().GetEntityAsync("ProcurementDemo", "OrderLines",
            Keys("""{"OrderNo":"PO-5001","LineNo":1}"""), new ODataQueryRequest(Select: ["PartNo", "Quantity"]), Ct);

        var entity = outcome.Data.AsObject();
        Assert.Equal(1, outcome.RowCount);
        Assert.Equal(["PartNo", "Quantity"], entity.Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task Get_entity_hides_denied_properties_and_odata_annotations()
    {
        var outcome = await Gateway().GetEntityAsync("ProcurementDemo", "Suppliers",
            Keys("""{"SupplierId":"S-1001"}"""), new ODataQueryRequest(), Ct);

        var json = outcome.Data.ToJsonString();
        Assert.Contains("Kestrel Fasteners", json);
        Assert.DoesNotContain("ContactEmail", json);
        Assert.DoesNotContain("@odata", json);
    }

    [Fact]
    public async Task Get_entity_of_a_missing_key_is_a_clear_upstream_error()
    {
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => Gateway().GetEntityAsync("ProcurementDemo", "Suppliers",
            Keys("""{"SupplierId":"S-DOES-NOT-EXIST"}"""), new ODataQueryRequest(), Ct));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Build_url_returns_the_validated_url_without_requesting_data()
    {
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => throw new InvalidOperationException("data must not be requested"));
        var gateway = TestSupport.Gateway(handler);

        var outcome = await gateway.BuildUrlAsync("ProcurementDemo", "PurchaseOrders",
            new ODataQueryRequest(Select: ["OrderNo"], Filter: "Status eq 'Closed'", Top: 7), null, Ct);

        Assert.Equal("GET", outcome.Method);
        Assert.Equal("http://localhost/odata/ProcurementDemo/PurchaseOrders?$select=OrderNo&$filter=Status%20eq%20'Closed'&$top=7", outcome.Url);
        Assert.All(handler.Requests, r => Assert.EndsWith("$metadata", r.Uri.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_url_supports_entity_urls_with_keys()
    {
        var outcome = await Gateway().BuildUrlAsync("ProcurementDemo", "OrderLines", new ODataQueryRequest(Select: ["PartNo"]),
            Keys("""{"OrderNo":"PO-5001","LineNo":2}"""), Ct);

        Assert.Equal("http://localhost/odata/ProcurementDemo/OrderLines(OrderNo='PO-5001',LineNo=2)?$select=PartNo", outcome.Url);
    }

    [Fact]
    public async Task Build_url_applies_the_same_validation_as_query()
    {
        await Assert.ThrowsAsync<PolicyViolationException>(() => Gateway().BuildUrlAsync("ProcurementDemo", "Suppliers",
            new ODataQueryRequest(Filter: "ContactEmail eq 'x'"), null, Ct));
    }

    [Fact]
    public async Task Responses_over_the_size_limit_are_rejected_by_content_length()
    {
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 5000)),
        });
        var gateway = TestSupport.Gateway(handler, TestSupport.Options(o => o.MaxResponseBytes = 1000));

        await Assert.ThrowsAsync<ResponseTooLargeException>(() => gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct));
    }

    [Fact]
    public async Task Streaming_responses_without_content_length_are_cut_off_at_the_limit()
    {
        var handler = StubHandler.Xml(MockData.ProcurementMetadata, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new string('x', 5000)),
        });
        var gateway = TestSupport.Gateway(handler, TestSupport.Options(o => o.MaxResponseBytes = 1000));

        await Assert.ThrowsAsync<ResponseTooLargeException>(() => gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct));
    }

    [Fact]
    public async Task Slow_responses_hit_the_request_timeout()
    {
        var handler = new SlowHandler();
        var gateway = TestSupport.Gateway(handler, TestSupport.Options(o => o.RequestTimeoutSeconds = 1));

        var ex = await Assert.ThrowsAsync<UpstreamException>(() => gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), Ct));

        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Gateway().QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), cts.Token));
    }

    private sealed class UnknownLengthContent(string text) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
