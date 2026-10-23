using System.Text.RegularExpressions;
using IfsODataMcp.Core.Metadata;
using IfsODataMcp.Core.Policy;

namespace IfsODataMcp.Core.Query;

/// <summary>Checks a $filter by tokenizing it instead of fully parsing it. Anything not recognized is rejected.</summary>
public static partial class FilterValidator
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "eq", "ne", "gt", "ge", "lt", "le", "and", "or", "not", "add", "sub", "mul", "div", "divby", "mod", "has", "in",
    };

    private static readonly HashSet<string> Literals = new(StringComparer.Ordinal) { "true", "false", "null", "NaN", "INF" };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "contains", "startswith", "endswith", "length", "indexof", "substring", "concat", "tolower", "toupper", "trim",
        "year", "month", "day", "hour", "minute", "second", "date", "time", "now", "round", "floor", "ceiling",
    };

    private static readonly HashSet<string> TypedLiteralPrefixes = new(StringComparer.Ordinal)
    {
        "duration", "guid", "datetime", "datetimeoffset", "binary", "time",
    };

    [GeneratedRegex(@"\G'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\G[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidLiteral();

    [GeneratedRegex(@"\G\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?(?![\w])")]
    private static partial Regex DateLiteral();

    [GeneratedRegex(@"\G-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[mMdDfFlL]?(?![\w.])")]
    private static partial Regex NumberLiteral();

    [GeneratedRegex(@"\G[A-Za-z_][A-Za-z0-9_]*(?:[./][A-Za-z_][A-Za-z0-9_]*)*")]
    private static partial Regex Identifier();

    public static void Validate(
        string filter,
        EntityTypeInfo type,
        ServiceMetadata metadata,
        AccessPolicy policy,
        Func<NavigationInfo, bool> isNavigationAllowed)
    {
        if (filter.Length > policy.Options.MaxFilterLength)
        {
            throw new QueryValidationException($"$filter is longer than the allowed {policy.Options.MaxFilterLength} characters.");
        }

        var depth = 0;
        var position = 0;
        while (position < filter.Length)
        {
            var c = filter[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '(') { depth++; position++; continue; }
            if (c == ')')
            {
                if (--depth < 0) throw new QueryValidationException("$filter has an unbalanced ')'.");
                position++;
                continue;
            }

            if (c == ',') { position++; continue; }

            if (TryMatch(StringLiteral(), filter, position, out var length)
                || TryMatch(GuidLiteral(), filter, position, out length)
                || TryMatch(DateLiteral(), filter, position, out length)
                || TryMatch(NumberLiteral(), filter, position, out length))
            {
                position += length;
                continue;
            }

            if (TryMatch(Identifier(), filter, position, out length))
            {
                var word = filter.Substring(position, length);
                position += length;
                var next = NextNonSpace(filter, position);

                // Typed literal such as duration'P1D' or Ns.Status'Open'.
                if (position < filter.Length && filter[position] == '\'' )
                {
                    if (!TypedLiteralPrefixes.Contains(word) && !word.Contains('.', StringComparison.Ordinal))
                    {
                        throw new QueryValidationException($"$filter contains an unsupported literal prefix '{word}'.");
                    }

                    if (!TryMatch(StringLiteral(), filter, position, out var litLength))
                    {
                        throw new QueryValidationException("$filter contains an unterminated string literal.");
                    }

                    position += litLength;
                    continue;
                }

                if (Operators.Contains(word))
                {
                    continue;
                }

                if (next == '(')
                {
                    if (!Functions.Contains(word))
                    {
                        throw new QueryValidationException(
                            $"$filter function '{word}' is not supported. Supported: {string.Join(", ", Functions.Order())}.");
                    }

                    continue;
                }

                if (Literals.Contains(word))
                {
                    continue;
                }

                ValidatePropertyPath(word, type, metadata, policy, isNavigationAllowed);
                continue;
            }

            throw new QueryValidationException($"$filter contains an unsupported character '{c}' at position {position}.");
        }

        if (depth != 0)
        {
            throw new QueryValidationException("$filter has an unbalanced '('.");
        }
    }

    private static void ValidatePropertyPath(
        string path, EntityTypeInfo type, ServiceMetadata metadata, AccessPolicy policy, Func<NavigationInfo, bool> isNavigationAllowed)
    {
        if (path.Contains('.', StringComparison.Ordinal))
        {
            throw new QueryValidationException($"$filter reference '{path}' is not a property of {type.Name}.");
        }

        var segments = path.Split('/');
        var current = type;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;

            if (isLast)
            {
                if (current.FindProperty(segment) is null)
                {
                    throw new QueryValidationException($"Unknown property '{segment}' in $filter for {current.Name}.");
                }

                if (policy.IsDenied(current, segment))
                {
                    throw new PolicyViolationException($"Property '{segment}' is denied by policy and cannot be used in $filter.");
                }

                return;
            }

            var nav = current.FindNavigation(segment)
                ?? throw new QueryValidationException($"Unknown navigation property '{segment}' in $filter for {current.Name}.");

            if (nav.IsCollection)
            {
                throw new QueryValidationException($"$filter cannot traverse the collection '{segment}' (any/all are not supported).");
            }

            if (i + 1 > policy.Options.MaxExpandDepth)
            {
                throw new QueryValidationException($"$filter path '{path}' is deeper than the allowed depth {policy.Options.MaxExpandDepth}.");
            }

            if (!isNavigationAllowed(nav))
            {
                throw new PolicyViolationException($"Navigation property '{segment}' leads to an entity set that is not allowed.");
            }

            current = metadata.FindType(nav.TargetType)
                ?? throw new QueryValidationException($"Navigation target '{nav.TargetType}' is unknown.");
        }
    }

    private static bool TryMatch(Regex regex, string input, int position, out int length)
    {
        var match = regex.Match(input, position);
        length = match.Success ? match.Length : 0;
        return match.Success && match.Index == position;
    }

    private static char NextNonSpace(string input, int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
        return position < input.Length ? input[position] : '\0';
    }
}
