using System.Text.RegularExpressions;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Metadata;
using IfsODataMcp.Core.Policy;

namespace IfsODataMcp.Core.Query;

/// <summary>Checks a query against the service metadata and the access policy and normalizes it.</summary>
public sealed partial class QueryValidator(AccessPolicy policy)
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*(?:/[A-Za-z_][A-Za-z0-9_]*)*)(?:\s+(asc|desc))?\s*$")]
    private static partial Regex OrderByItemRegex();

    public ValidatedQuery Validate(
        ProjectionRule rule, ServiceMetadata metadata, EntitySetInfo entitySet, ODataQueryRequest request)
    {
        var type = entitySet.EntityType;
        var warnings = new List<string>();

        var select = ValidateSelect(type, request.Select, warnings);

        string? filter = null;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            filter = request.Filter.Trim();
            FilterValidator.Validate(filter, type, metadata, policy, nav => policy.IsNavigationAllowed(rule, metadata, nav));
        }

        var orderBy = ValidateOrderBy(type, request.OrderBy);
        var top = ValidateTop(request.Top, warnings);
        var expand = ValidateExpand(rule, metadata, type, request.Expand);

        return new ValidatedQuery(select, filter, orderBy, top, expand, warnings);
    }

    /// <summary>Validates and normalizes a select list. An empty list means every property the policy allows.</summary>
    public IReadOnlyList<string> ValidateSelect(EntityTypeInfo type, IReadOnlyList<string>? requested, List<string> warnings)
    {
        var all = policy.AllowedProperties(type).Select(p => p.Name).ToList();
        var cleaned = (requested ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (cleaned.Count == 0 || cleaned.Contains("*"))
        {
            return all;
        }

        var result = new List<string>();
        var denied = new List<string>();
        foreach (var name in cleaned)
        {
            if (!SimpleName().IsMatch(name) || type.FindProperty(name) is null)
            {
                throw new QueryValidationException(
                    $"Unknown property '{Shorten(name)}' in $select for {type.Name}. Use describe_entity_set to list properties.");
            }

            if (policy.IsDenied(type, name)) denied.Add(name);
            else result.Add(name);
        }

        if (denied.Count > 0)
        {
            if (result.Count == 0)
            {
                throw new PolicyViolationException("Every property in $select is denied by policy.");
            }

            warnings.Add($"Removed {denied.Count} property(ies) from $select because policy denies them: {string.Join(", ", denied)}.");
        }

        return result;
    }

    private IReadOnlyList<OrderByItem> ValidateOrderBy(EntityTypeInfo type, string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy)) return [];

        var items = new List<OrderByItem>();
        foreach (var part in orderBy.Split(','))
        {
            var match = OrderByItemRegex().Match(part);
            if (!match.Success)
            {
                throw new QueryValidationException($"Invalid $orderby item '{Shorten(part.Trim())}'. Use 'Property [asc|desc]'.");
            }

            var property = match.Groups[1].Value;
            if (property.Contains('/'))
            {
                throw new QueryValidationException("$orderby on navigation paths is not supported.");
            }

            if (type.FindProperty(property) is null)
            {
                throw new QueryValidationException($"Unknown property '{property}' in $orderby for {type.Name}.");
            }

            if (policy.IsDenied(type, property))
            {
                throw new PolicyViolationException($"Property '{property}' is denied by policy and cannot be used in $orderby.");
            }

            items.Add(new OrderByItem(property, match.Groups[2].Value == "desc"));
        }

        return items;
    }

    private int ValidateTop(int? requested, List<string> warnings)
    {
        var options = policy.Options;
        if (requested is null) return options.DefaultTop;
        if (requested < 1) throw new QueryValidationException("top must be at least 1.");
        if (requested > options.MaxTop)
        {
            warnings.Add($"top was reduced from {requested} to the maximum of {options.MaxTop}.");
            return options.MaxTop;
        }

        return requested.Value;
    }

    private IReadOnlyList<ExpandNode> ValidateExpand(
        ProjectionRule rule, ServiceMetadata metadata, EntityTypeInfo type, IReadOnlyList<string>? requested)
    {
        var paths = (requested ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return [];

        var root = new MutableNode("", type);
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            if (segments.Length > policy.Options.MaxExpandDepth)
            {
                throw new QueryValidationException(
                    $"$expand '{Shorten(path)}' is deeper than the allowed depth {policy.Options.MaxExpandDepth}.");
            }

            var current = root;
            foreach (var segment in segments)
            {
                if (!SimpleName().IsMatch(segment))
                {
                    throw new QueryValidationException(
                        $"Invalid $expand '{Shorten(path)}'. Only plain navigation property names are supported.");
                }

                var nav = current.Type.FindNavigation(segment)
                    ?? throw new QueryValidationException($"Unknown navigation property '{segment}' on {current.Type.Name}.");

                if (!policy.IsNavigationAllowed(rule, metadata, nav))
                {
                    throw new PolicyViolationException($"Navigation property '{segment}' leads to an entity set that is not allowed.");
                }

                if (!current.Children.TryGetValue(segment, out var child))
                {
                    var target = metadata.FindType(nav.TargetType)
                        ?? throw new QueryValidationException($"Navigation target '{nav.TargetType}' is unknown.");
                    child = new MutableNode(segment, target);
                    current.Children[segment] = child;
                }

                current = child;
            }
        }

        return root.Children.Values.Select(Freeze).ToList();
    }

    private ExpandNode Freeze(MutableNode node) =>
        new(node.Name,
            policy.AllowedProperties(node.Type).Select(p => p.Name).ToList(),
            node.Children.Values.Select(Freeze).ToList());

    private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "...";

    private sealed class MutableNode(string name, EntityTypeInfo type)
    {
        public string Name { get; } = name;
        public EntityTypeInfo Type { get; } = type;
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.Ordinal);
    }
}
