using IfsODataMcp.Core;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Policy;

namespace IfsODataMcp.Tests;

public class PolicyTests
{
    [Fact]
    public void Empty_configuration_allows_nothing()
    {
        var policy = new AccessPolicy(new PolicyOptions());

        Assert.Throws<PolicyViolationException>(() => policy.EnsureProjectionAllowed("ProcurementDemo"));
    }

    [Theory]
    [InlineData("StaffDirectoryDemo")]
    [InlineData("procurementdemo")]
    [InlineData("../ProcurementDemo")]
    [InlineData("ProcurementDemo/Suppliers")]
    [InlineData("")]
    [InlineData(null)]
    public void Projection_outside_allow_list_is_rejected(string? name)
    {
        var policy = new AccessPolicy(TestSupport.Options());

        var ex = Assert.Throws<PolicyViolationException>(() => policy.EnsureProjectionAllowed(name));
        Assert.Equal("policy_violation", ex.Code);
    }

    [Fact]
    public void Allowed_projection_is_returned()
    {
        var policy = new AccessPolicy(TestSupport.Options());

        Assert.Equal("ProcurementDemo", policy.EnsureProjectionAllowed("ProcurementDemo").Name);
    }

    [Fact]
    public void Entity_set_allow_list_is_enforced_when_configured()
    {
        var policy = new AccessPolicy(TestSupport.Options(o => o.Projections[0].EntitySets = ["Suppliers"]));
        var rule = policy.EnsureProjectionAllowed("ProcurementDemo");

        AccessPolicy.EnsureEntitySetAllowed(rule, "Suppliers");
        Assert.Throws<PolicyViolationException>(() => AccessPolicy.EnsureEntitySetAllowed(rule, "PurchaseOrders"));
    }

    [Fact]
    public void Missing_entity_set_allow_list_means_all_sets()
    {
        var policy = new AccessPolicy(TestSupport.Options());
        var rule = policy.EnsureProjectionAllowed("ProcurementDemo");

        AccessPolicy.EnsureEntitySetAllowed(rule, "PurchaseOrders");
    }

    [Fact]
    public void Deny_list_supports_global_and_scoped_entries()
    {
        var policy = new AccessPolicy(TestSupport.Options(o => o.DeniedProperties = ["Note", "Suppliers.Rating"]));
        var metadata = TestSupport.Metadata();
        var supplier = metadata.FindEntitySet("Suppliers")!.EntityType;
        var order = metadata.FindEntitySet("PurchaseOrders")!.EntityType;

        Assert.True(policy.IsDenied(order, "Note"));
        Assert.True(policy.IsDenied(supplier, "Rating"));
        Assert.False(policy.IsDenied(order, "Rating"));
        Assert.False(policy.IsDenied(order, "Status"));
    }

    [Fact]
    public void Scoped_deny_entry_matches_entity_type_name_too()
    {
        var policy = new AccessPolicy(TestSupport.Options(o => o.DeniedProperties = ["Supplier.Rating"]));
        var supplier = TestSupport.Metadata().FindEntitySet("Suppliers")!.EntityType;

        Assert.True(policy.IsDenied(supplier, "Rating"));
    }

    [Fact]
    public void Navigation_to_a_disallowed_entity_set_is_not_allowed()
    {
        var policy = new AccessPolicy(TestSupport.Options(o => o.Projections[0].EntitySets = ["PurchaseOrders", "Suppliers"]));
        var metadata = TestSupport.Metadata();
        var rule = policy.EnsureProjectionAllowed("ProcurementDemo");
        var orders = metadata.FindEntitySet("PurchaseOrders")!.EntityType;

        Assert.True(AccessPolicy.IsNavigationAllowed(rule, metadata, orders.FindNavigation("Supplier")!));
        Assert.False(AccessPolicy.IsNavigationAllowed(rule, metadata, orders.FindNavigation("Lines")!));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(60, 50)]
    public void Inconsistent_top_limits_are_rejected_at_startup(int defaultTop, int maxTop)
    {
        Assert.Throws<ConfigurationException>(() =>
            new AccessPolicy(new PolicyOptions { DefaultTop = defaultTop, MaxTop = maxTop }));
    }

    [Fact]
    public void Safe_defaults_match_the_documented_values()
    {
        var options = new PolicyOptions();

        Assert.Equal(50, options.DefaultTop);
        Assert.Equal(200, options.MaxTop);
        Assert.Equal(1, options.MaxExpandDepth);
        Assert.Empty(options.Projections);
    }
}
