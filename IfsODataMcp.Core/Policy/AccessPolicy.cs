using System.Text.RegularExpressions;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Metadata;

namespace IfsODataMcp.Core.Policy;

/// <summary>Answers "may the agent touch this?" for projections, entity sets and properties.</summary>
public sealed partial class AccessPolicy
{
    private readonly PolicyOptions _options;
    private readonly Dictionary<string, ProjectionRule> _projections;
    private readonly List<(string? Scope, string Property)> _denied;

    public AccessPolicy(PolicyOptions options)
    {
        _options = options;
        _projections = options.Projections
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToDictionary(p => p.Name.Trim(), StringComparer.Ordinal);
        _denied = options.DeniedProperties.Select(ParseDenyEntry).ToList();

        if (options.DefaultTop < 1 || options.MaxTop < options.DefaultTop)
        {
            throw new ConfigurationException("Policy: require 1 <= DefaultTop <= MaxTop.");
        }
    }

    public PolicyOptions Options => _options;

    public IReadOnlyCollection<ProjectionRule> Projections => _projections.Values;

    public ProjectionRule EnsureProjectionAllowed(string? projection)
    {
        // The name ends up inside a URL, so the shape is checked before the allow-list lookup.
        if (string.IsNullOrWhiteSpace(projection) || !ProjectionNameRegex().IsMatch(projection)
            || !_projections.TryGetValue(projection, out var rule))
        {
            throw new PolicyViolationException(
                $"Projection '{Truncate(projection)}' is not in the allow-list. Use list_projections to see what is available.");
        }

        return rule;
    }

    public static bool IsEntitySetAllowed(ProjectionRule rule, string entitySet) =>
        rule.EntitySets is not { Count: > 0 } || rule.EntitySets.Contains(entitySet, StringComparer.Ordinal);

    public static void EnsureEntitySetAllowed(ProjectionRule rule, string? entitySet)
    {
        if (string.IsNullOrWhiteSpace(entitySet) || !IsEntitySetAllowed(rule, entitySet))
        {
            throw new PolicyViolationException(
                $"Entity set '{Truncate(entitySet)}' is not allowed in projection '{rule.Name}'.");
        }
    }

    /// <summary>True if the property of <paramref name="type"/> is on the deny-list.</summary>
    public bool IsDenied(EntityTypeInfo type, string property) =>
        _denied.Any(d => d.Property == property
            && (d.Scope is null || d.Scope == type.Name || type.EntitySetNames.Contains(d.Scope)));

    /// <summary>True if the name is denied for every entity type (used where the type is unknown, e.g. complex values).</summary>
    public bool IsDeniedEverywhere(string property) => _denied.Any(d => d.Scope is null && d.Property == property);

    /// <summary>Properties of <paramref name="type"/> an agent may see, in metadata order.</summary>
    public IEnumerable<PropertyInfo> AllowedProperties(EntityTypeInfo type) =>
        type.Properties.Where(p => !IsDenied(type, p.Name));

    /// <summary>
    /// A navigation property is usable only if its target type lives in at least one allowed entity set.
    /// Otherwise expanding it would leak data from an entity set the policy excludes.
    /// </summary>
    public static bool IsNavigationAllowed(ProjectionRule rule, ServiceMetadata metadata, NavigationInfo navigation)
    {
        var target = metadata.FindType(navigation.TargetType);
        return target is not null && target.EntitySetNames.Any(s => IsEntitySetAllowed(rule, s));
    }

    private static (string? Scope, string Property) ParseDenyEntry(string entry)
    {
        var trimmed = entry.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot < 0 ? (null, trimmed) : (trimmed[..dot], trimmed[(dot + 1)..]);
    }

    private static string Truncate(string? value) =>
        value is null ? "" : value.Length <= 60 ? value : value[..60] + "...";

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,120}$")]
    private static partial Regex ProjectionNameRegex();
}
