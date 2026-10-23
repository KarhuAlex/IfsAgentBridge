using IfsODataMcp.Core;
using IfsODataMcp.Core.Query;

namespace IfsODataMcp.Tests;

public class QueryValidationTests
{
    private static ValidatedQuery Validate(ODataQueryRequest request, Action<IfsODataMcp.Core.Configuration.PolicyOptions>? configure = null) =>
        TestSupport.Validate(request, TestSupport.Options(configure));

    [Fact]
    public void Top_defaults_to_50()
    {
        Assert.Equal(50, Validate(new ODataQueryRequest()).Top);
    }

    [Fact]
    public void Top_above_the_cap_is_clamped_to_200_with_a_warning()
    {
        var query = Validate(new ODataQueryRequest(Top: 5000));

        Assert.Equal(200, query.Top);
        Assert.Contains(query.Warnings, w => w.Contains("200"));
    }

    [Fact]
    public void Top_within_the_cap_is_kept()
    {
        var query = Validate(new ODataQueryRequest(Top: 75));

        Assert.Equal(75, query.Top);
        Assert.Empty(query.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_top_is_rejected(int top)
    {
        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(Top: top)));
    }

    [Fact]
    public void Configured_cap_is_respected()
    {
        var query = Validate(new ODataQueryRequest(Top: 80), o => { o.DefaultTop = 10; o.MaxTop = 20; });

        Assert.Equal(20, query.Top);
    }

    [Fact]
    public void Empty_select_expands_to_all_allowed_properties()
    {
        var query = TestSupport.Validate(new ODataQueryRequest(), TestSupport.Options(o => o.DeniedProperties = ["Note"]));

        Assert.Contains("OrderNo", query.Select);
        Assert.DoesNotContain("Note", query.Select);
    }

    [Fact]
    public void Wildcard_select_never_includes_denied_properties()
    {
        var query = TestSupport.Validate(new ODataQueryRequest(Select: ["*"]), TestSupport.Options(o => o.DeniedProperties = ["Note"]));

        Assert.DoesNotContain("Note", query.Select);
    }

    [Fact]
    public void Denied_property_is_removed_from_select_with_a_warning()
    {
        var query = Validate(new ODataQueryRequest(Select: ["OrderNo", "Note"]), o => o.DeniedProperties = ["Note"]);

        Assert.Equal(["OrderNo"], query.Select);
        Assert.Contains(query.Warnings, w => w.Contains("Note"));
    }

    [Fact]
    public void Select_of_only_denied_properties_is_a_policy_violation()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(Select: ["Note"]), o => o.DeniedProperties = ["Note"]));
    }

    [Theory]
    [InlineData("DoesNotExist")]
    [InlineData("orderno")]
    [InlineData("OrderNo;drop")]
    [InlineData("Supplier/Name")]
    public void Unknown_or_malformed_select_entries_are_rejected(string property)
    {
        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(Select: [property])));
    }

    [Theory]
    [InlineData("Status eq 'Closed'")]
    [InlineData("Status eq 'it''s fine'")]
    [InlineData("TotalAmount gt 100.5 and Status ne 'Planned'")]
    [InlineData("(Status eq 'Closed' or Status eq 'Released') and TotalAmount le 1000")]
    [InlineData("contains(Note,'terms')")]
    [InlineData("startswith(tolower(Status),'cl')")]
    [InlineData("OrderDate ge 2025-03-01")]
    [InlineData("OrderDate ge 2025-03-01T10:00:00Z")]
    [InlineData("not (Status eq 'Closed')")]
    [InlineData("Supplier/Country eq 'SE'")]
    [InlineData("Status eq null")]
    [InlineData("TotalAmount gt -5")]
    [InlineData("Status in ('Closed','Planned')")]
    public void Valid_filters_are_accepted(string filter)
    {
        var query = Validate(new ODataQueryRequest(Filter: filter));

        Assert.Equal(filter, query.Filter);
    }

    [Theory]
    [InlineData("Bogus eq 1")]
    [InlineData("Supplier/Bogus eq 1")]
    [InlineData("Bogus/Name eq 'x'")]
    [InlineData("Status eq 'a' ; drop")]
    [InlineData("Status eq 'a' & $top=100000")]
    [InlineData("$it/Status eq 'a'")]
    [InlineData("Lines/any(l: l/Quantity gt 1)")]
    [InlineData("Lines/Quantity gt 1")]
    [InlineData("evilfunc(Status) eq 'a'")]
    [InlineData("Status eq 'unterminated")]
    [InlineData("(Status eq 'a'")]
    [InlineData("Status eq 'a')")]
    [InlineData("Status eq @param")]
    [InlineData("Status eq {1}")]
    public void Invalid_filters_are_rejected(string filter)
    {
        Assert.ThrowsAny<BridgeException>(() => Validate(new ODataQueryRequest(Filter: filter)));
    }

    [Fact]
    public void Filter_on_a_denied_property_is_a_policy_violation()
    {
        // Filtering on a hidden field would let the agent infer its value one query at a time.
        var ex = Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(Filter: "Note eq 'secret'"), o => o.DeniedProperties = ["Note"]));

        Assert.Contains("Note", ex.Message);
    }

    [Fact]
    public void Filter_on_a_denied_property_of_a_related_entity_is_a_policy_violation()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(Filter: "Supplier/ContactEmail eq 'x'")));
    }

    [Fact]
    public void Filter_through_a_navigation_to_a_disallowed_entity_set_is_rejected()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(Filter: "Supplier/Country eq 'SE'"), o => o.Projections[0].EntitySets = ["PurchaseOrders"]));
    }

    [Fact]
    public void Overlong_filter_is_rejected()
    {
        var filter = string.Join(" or ", Enumerable.Repeat("Status eq 'Closed'", 200));

        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(Filter: filter)));
    }

    [Fact]
    public void Orderby_is_parsed_with_directions()
    {
        var query = Validate(new ODataQueryRequest(OrderBy: "OrderDate desc, Status"));

        Assert.Equal([new OrderByItem("OrderDate", true), new OrderByItem("Status", false)], query.OrderBy);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("Status sideways")]
    [InlineData("Status desc, Status; drop")]
    [InlineData("Supplier/Name")]
    public void Invalid_orderby_is_rejected(string orderBy)
    {
        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(OrderBy: orderBy)));
    }

    [Fact]
    public void Orderby_on_a_denied_property_is_rejected()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(OrderBy: "Note"), o => o.DeniedProperties = ["Note"]));
    }

    [Fact]
    public void Expand_selects_only_allowed_properties_of_the_related_type()
    {
        var query = Validate(new ODataQueryRequest(Expand: ["Supplier"]));

        var supplier = Assert.Single(query.Expand);
        Assert.Contains("Name", supplier.Select);
        Assert.DoesNotContain("ContactEmail", supplier.Select);
        Assert.DoesNotContain("ContactPhone", supplier.Select);
    }

    [Fact]
    public void Expand_depth_above_the_default_of_one_is_rejected()
    {
        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(Expand: ["Lines/Order"])));
    }

    [Fact]
    public void Deeper_expand_is_possible_when_configured()
    {
        var query = Validate(new ODataQueryRequest(Expand: ["Lines/Order"]), o => o.MaxExpandDepth = 2);

        var lines = Assert.Single(query.Expand);
        Assert.Equal("Order", Assert.Single(lines.Children).Navigation);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("Supplier($select=ContactEmail)")]
    [InlineData("Supplier,Bogus")]
    [InlineData("*")]
    public void Unknown_or_complex_expand_is_rejected(string expand)
    {
        Assert.Throws<QueryValidationException>(() => Validate(new ODataQueryRequest(Expand: expand.Split(','))));
    }

    [Fact]
    public void Expand_into_a_disallowed_entity_set_is_a_policy_violation()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Validate(new ODataQueryRequest(Expand: ["Lines"]), o => o.Projections[0].EntitySets = ["PurchaseOrders", "Suppliers"]));
    }

    [Fact]
    public void Parameter_names_list_only_supplied_parameters()
    {
        var request = new ODataQueryRequest(Select: ["OrderNo"], Filter: "Status eq 'x'", Top: 5);

        Assert.Equal(["select", "filter", "top"], request.ParameterNames());
    }
}
