namespace IfsODataMcp.Core.Query;

/// <summary>What the caller asked for. Not trusted until it has been through <see cref="QueryValidator"/>.</summary>
public sealed record ODataQueryRequest(
    IReadOnlyList<string>? Select = null,
    string? Filter = null,
    string? OrderBy = null,
    int? Top = null,
    IReadOnlyList<string>? Expand = null)
{
    /// <summary>Names (never values) of the parameters that were supplied. Used for the audit log.</summary>
    public IReadOnlyList<string> ParameterNames()
    {
        var names = new List<string>();
        if (Select is { Count: > 0 }) names.Add("select");
        if (!string.IsNullOrWhiteSpace(Filter)) names.Add("filter");
        if (!string.IsNullOrWhiteSpace(OrderBy)) names.Add("orderby");
        if (Top is not null) names.Add("top");
        if (Expand is { Count: > 0 }) names.Add("expand");
        return names;
    }
}

public sealed record OrderByItem(string Property, bool Descending);

/// <summary>One expanded navigation property. Its select list is always explicit so denied properties are never fetched.</summary>
public sealed record ExpandNode(string Navigation, IReadOnlyList<string> Select, IReadOnlyList<ExpandNode> Children);

/// <summary>A query that passed validation and may be turned into a URL.</summary>
public sealed record ValidatedQuery(
    IReadOnlyList<string> Select,
    string? Filter,
    IReadOnlyList<OrderByItem> OrderBy,
    int Top,
    IReadOnlyList<ExpandNode> Expand,
    IReadOnlyList<string> Warnings);
