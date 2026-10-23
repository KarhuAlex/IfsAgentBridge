using System.Text.Json;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Query;

namespace IfsODataMcp.Tests;

public class UrlBuilderTests
{
    private static readonly ODataUrlBuilder Urls = new(TestSupport.Endpoint());

    private static IReadOnlyDictionary<string, JsonElement> Keys(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Entity_set_url_contains_validated_options()
    {
        var query = TestSupport.Validate(new ODataQueryRequest(
            Select: ["OrderNo", "Status"], Filter: "Status eq 'Closed'", OrderBy: "OrderDate desc", Top: 10));

        var url = Urls.EntitySet("ProcurementDemo", "PurchaseOrders", query);

        Assert.Equal(
            "http://localhost/odata/ProcurementDemo/PurchaseOrders?$select=OrderNo,Status&$filter=Status%20eq%20'Closed'&$orderby=OrderDate%20desc&$top=10",
            url.AbsoluteUri);
    }

    [Fact]
    public void Filter_values_cannot_inject_additional_query_options()
    {
        var query = TestSupport.Validate(new ODataQueryRequest(Filter: "Status eq 'a&$top=99999#x'"));

        var url = Urls.EntitySet("ProcurementDemo", "PurchaseOrders", query);

        Assert.DoesNotContain('#', url.OriginalString);
        Assert.Single(url.Query.Split('&'), p => p.StartsWith("$top=", StringComparison.Ordinal));
        Assert.DoesNotContain("&$top=99999", url.Query);
        Assert.Contains("%26%24top%3D99999%23x", url.Query);
    }

    [Fact]
    public void Expand_is_rendered_with_an_explicit_nested_select()
    {
        var query = TestSupport.Validate(new ODataQueryRequest(Select: ["OrderNo"], Expand: ["Supplier"]));

        var url = Urls.EntitySet("ProcurementDemo", "PurchaseOrders", query);

        Assert.Contains("$expand=Supplier($select=SupplierId,Name,Country,Rating,Active)", url.Query);
        Assert.DoesNotContain("ContactEmail", url.Query);
    }

    [Fact]
    public void Single_key_is_rendered_as_a_quoted_string()
    {
        var type = TestSupport.Metadata().FindEntitySet("Suppliers")!.EntityType;

        Assert.Equal("('S-1001')", KeyPredicateBuilder.Build(type, Keys("""{"SupplierId":"S-1001"}""")));
    }

    [Fact]
    public void Composite_key_is_rendered_in_metadata_order_with_types()
    {
        var type = TestSupport.Metadata().FindEntitySet("OrderLines")!.EntityType;

        Assert.Equal("(OrderNo='PO-5001',LineNo=2)", KeyPredicateBuilder.Build(type, Keys("""{"LineNo":2,"OrderNo":"PO-5001"}""")));
    }

    [Fact]
    public void Quotes_in_key_values_are_doubled()
    {
        var type = TestSupport.Metadata().FindEntitySet("Suppliers")!.EntityType;

        Assert.Equal("('O''Brien')", KeyPredicateBuilder.Build(type, Keys("""{"SupplierId":"O'Brien"}""")));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"OrderNo":"PO-1"}""")]
    [InlineData("""{"OrderNo":"PO-1","LineNo":"abc"}""")]
    [InlineData("""{"OrderNo":"PO-1","LineNo":1.5}""")]
    [InlineData("""{"OrderNo":"PO-1","LineNo":1,"Extra":1}""")]
    [InlineData("""{"OrderNo":null,"LineNo":1}""")]
    public void Bad_keys_are_rejected(string json)
    {
        var type = TestSupport.Metadata().FindEntitySet("OrderLines")!.EntityType;

        Assert.Throws<QueryValidationException>(() => KeyPredicateBuilder.Build(type, Keys(json)));
    }

    [Fact]
    public void Entity_url_encodes_slashes_in_key_values()
    {
        var type = TestSupport.Metadata().FindEntitySet("Suppliers")!.EntityType;
        var predicate = KeyPredicateBuilder.Build(type, Keys("""{"SupplierId":"a/b"}"""));

        var url = Urls.Entity("ProcurementDemo", "Suppliers", predicate, ["Name"], []);

        Assert.Equal("http://localhost/odata/ProcurementDemo/Suppliers('a%2Fb')?$select=Name", url.AbsoluteUri);
    }

    [Theory]
    [InlineData("", "{projection}")]
    [InlineData("https://erp.example.com", "")]
    [InlineData("https://erp.example.com", "no-placeholder")]
    [InlineData("not a url", "{baseUrl}/{projection}/")]
    [InlineData("ftp://erp.example.com", "{baseUrl}/{projection}/")]
    public void Url_templates_are_validated(string baseUrl, string template)
    {
        Assert.Throws<ConfigurationException>(() =>
            new ODataUrlBuilder(new ODataEndpointOptions { BaseUrl = baseUrl, ServiceRootTemplate = template }));
    }

    [Fact]
    public void Service_root_comes_entirely_from_configuration()
    {
        var urls = new ODataUrlBuilder(new ODataEndpointOptions
        {
            BaseUrl = "https://erp.example.test/",
            ServiceRootTemplate = "{baseUrl}/custom/path/{projection}.svc",
        });

        Assert.Equal("https://erp.example.test/custom/path/Demo.svc/$metadata", urls.Metadata("Demo").AbsoluteUri);
    }
}
