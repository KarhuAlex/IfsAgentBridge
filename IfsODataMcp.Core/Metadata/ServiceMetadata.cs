namespace IfsODataMcp.Core.Metadata;

public sealed record PropertyInfo(string Name, string Type, bool Nullable, bool IsKey);

public sealed record NavigationInfo(string Name, string TargetType, bool IsCollection);

public sealed class EntityTypeInfo
{
    public EntityTypeInfo(
        string name,
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyList<NavigationInfo> navigations)
    {
        Name = name;
        Properties = properties;
        Navigations = navigations;
    }

    public string Name { get; }

    public IReadOnlyList<PropertyInfo> Properties { get; }

    public IReadOnlyList<NavigationInfo> Navigations { get; }

    public IReadOnlyList<string> EntitySetNames { get; internal set; } = [];

    public IEnumerable<PropertyInfo> Keys => Properties.Where(p => p.IsKey);

    public PropertyInfo? FindProperty(string name) => Properties.FirstOrDefault(p => p.Name == name);

    public NavigationInfo? FindNavigation(string name) => Navigations.FirstOrDefault(n => n.Name == name);
}

public sealed record EntitySetInfo(string Name, EntityTypeInfo EntityType);

public sealed class ServiceMetadata
{
    private readonly Dictionary<string, EntitySetInfo> _sets;
    private readonly Dictionary<string, EntityTypeInfo> _types;

    public ServiceMetadata(IEnumerable<EntityTypeInfo> types, IEnumerable<(string Name, string TypeName)> sets)
    {
        _types = types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _sets = sets.ToDictionary(
            s => s.Name,
            s => new EntitySetInfo(s.Name, _types[s.TypeName]),
            StringComparer.Ordinal);

        foreach (var type in _types.Values)
        {
            type.EntitySetNames = _sets.Values.Where(s => s.EntityType == type).Select(s => s.Name).ToList();
        }
    }

    public IReadOnlyCollection<EntitySetInfo> EntitySets => _sets.Values;

    public EntitySetInfo? FindEntitySet(string name) => _sets.GetValueOrDefault(name);

    public EntityTypeInfo? FindType(string name) => _types.GetValueOrDefault(name);
}
