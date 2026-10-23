namespace IfsODataMcp.Core.Query;

public sealed record ODataQueryRequest(
    IReadOnlyList<string>? Select = null,
    string? Filter = null,
    string? OrderBy = null,
    int? Top = null,
    IReadOnlyList<string>? Expand = null)
{
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

public sealed record ExpandNode(string Navigation, IReadOnlyList<string> Select, IReadOnlyList<ExpandNode> Children);

public sealed record ValidatedQuery(
    IReadOnlyList<string> Select,
    string? Filter,
    IReadOnlyList<OrderByItem> OrderBy,
    int Top,
    IReadOnlyList<ExpandNode> Expand,
    IReadOnlyList<string> Warnings);
