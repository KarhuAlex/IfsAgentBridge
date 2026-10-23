using System.Globalization;
using System.Text.RegularExpressions;

namespace MockODataService;

using Row = Dictionary<string, object?>;

/// <summary>Evaluates the OData options the demo needs: select, filter, orderby, top, skip and expand.</summary>
public static partial class MockQueryEngine
{
    public const int ServerPageSize = 100;

    public static (List<Row> Rows, bool HasMore) Query(
        ProjectionDef projection, EntitySetDef set, IReadOnlyDictionary<string, string> options)
    {
        IEnumerable<Row> rows = set.Rows;

        if (options.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            var predicate = new FilterParser(filter, projection, set).Parse();
            rows = rows.Where(r => predicate(r) is true);
        }

        if (options.TryGetValue("$orderby", out var orderBy) && !string.IsNullOrWhiteSpace(orderBy))
        {
            IOrderedEnumerable<Row>? ordered = null;
            foreach (var part in orderBy.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var name = pieces[0];
                var descending = pieces.Length > 1 && pieces[1] == "desc";
                Func<Row, object?> key = r => r.GetValueOrDefault(name);
                ordered = ordered is null
                    ? (descending ? rows.OrderByDescending(key, ValueComparer.Instance) : rows.OrderBy(key, ValueComparer.Instance))
                    : (descending ? ordered.ThenByDescending(key, ValueComparer.Instance) : ordered.ThenBy(key, ValueComparer.Instance));
            }

            rows = ordered ?? rows;
        }

        if (options.TryGetValue("$skip", out var skip) && int.TryParse(skip, out var skipCount))
        {
            rows = rows.Skip(skipCount);
        }

        var hasMore = false;
        var materialized = rows.ToList();
        if (options.TryGetValue("$top", out var top) && int.TryParse(top, out var topCount))
        {
            materialized = materialized.Take(topCount).ToList();
        }
        else if (materialized.Count > ServerPageSize)
        {
            materialized = materialized.Take(ServerPageSize).ToList();
            hasMore = true;
        }

        options.TryGetValue("$select", out var select);
        options.TryGetValue("$expand", out var expand);
        return (materialized.Select(r => Shape(projection, set, r, select, expand)).ToList(), hasMore);
    }

    public static Row? FindByKey(EntitySetDef set, string predicate)
    {
        var inner = predicate.Trim();
        var pairs = SplitTopLevel(inner, ',');
        var values = new Dictionary<string, string>();
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq < 0 && set.Keys.Length == 1) values[set.Keys[0]] = pair.Trim();
            else if (eq > 0) values[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        return set.Rows.FirstOrDefault(r => set.Keys.All(k =>
            values.TryGetValue(k, out var literal) && FilterParser.CompareValues(r[k], FilterParser.ParseLiteral(literal)) == 0));
    }

    public static Row Shape(ProjectionDef projection, EntitySetDef set, Row source, string? select, string? expand)
    {
        var result = new Row();
        var selected = string.IsNullOrWhiteSpace(select) || select == "*"
            ? null
            : select.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        foreach (var (name, value) in source)
        {
            if (selected is null || selected.Contains(name))
            {
                result[name] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(expand))
        {
            foreach (var item in SplitTopLevel(expand, ','))
            {
                var open = item.IndexOf('(');
                var nav = (open < 0 ? item : item[..open]).Trim();
                if (!set.Navs.TryGetValue(nav, out var def)) continue;

                string? nestedSelect = null, nestedExpand = null;
                if (open >= 0)
                {
                    var body = item[(open + 1)..item.LastIndexOf(')')];
                    foreach (var option in SplitTopLevel(body, ';'))
                    {
                        if (option.StartsWith("$select=")) nestedSelect = option["$select=".Length..];
                        if (option.StartsWith("$expand=")) nestedExpand = option["$expand=".Length..];
                    }
                }

                var target = projection.Sets[def.TargetSet];
                var related = target.Rows.Where(t => def.Match(source, t))
                    .Select(t => Shape(projection, target, t, nestedSelect, nestedExpand)).ToList();
                result[nav] = def.IsCollection ? related : related.FirstOrDefault();
            }
        }

        return result;
    }

    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y) => FilterParser.CompareValues(x, y);
    }

    private sealed partial class FilterParser(string text, ProjectionDef projection, EntitySetDef set)
    {
        private List<string> _tokens = [];
        private int _position;

        [GeneratedRegex(@"'(?:[^']|'')*'|\d{4}-\d{2}-\d{2}|[A-Za-z_][A-Za-z0-9_/.]*|-?\d+(?:\.\d+)?|[(),]")]
        private static partial Regex TokenRegex();

        public Func<Row, object?> Parse()
        {
            _tokens = TokenRegex().Matches(text).Select(m => m.Value).ToList();
            var expr = ParseOr();
            return expr;
        }

        private string? Peek => _position < _tokens.Count ? _tokens[_position] : null;

        private Func<Row, object?> ParseOr()
        {
            var left = ParseAnd();
            while (Peek == "or")
            {
                _position++;
                var l = left;
                var right = ParseAnd();
                left = r => (l(r) as bool? ?? false) || (right(r) as bool? ?? false);
            }

            return left;
        }

        private Func<Row, object?> ParseAnd()
        {
            var left = ParseNot();
            while (Peek == "and")
            {
                _position++;
                var l = left;
                var right = ParseNot();
                left = r => (l(r) as bool? ?? false) && (right(r) as bool? ?? false);
            }

            return left;
        }

        private Func<Row, object?> ParseNot()
        {
            if (Peek == "not")
            {
                _position++;
                var inner = ParseNot();
                return r => !(inner(r) as bool? ?? false);
            }

            return ParseComparison();
        }

        private Func<Row, object?> ParseComparison()
        {
            var left = ParsePrimary();
            if (Peek is "eq" or "ne" or "gt" or "ge" or "lt" or "le")
            {
                var op = _tokens[_position++];
                var right = ParsePrimary();
                return r =>
                {
                    var a = left(r);
                    var b = right(r);
                    if (a is null || b is null) return op switch { "eq" => a is null && b is null, "ne" => !(a is null && b is null), _ => false };
                    var c = CompareValues(a, b);
                    return op switch { "eq" => c == 0, "ne" => c != 0, "gt" => c > 0, "ge" => c >= 0, "lt" => c < 0, _ => c <= 0 };
                };
            }

            return left;
        }

        private Func<Row, object?> ParsePrimary()
        {
            var token = _tokens[_position++];
            if (token == "(")
            {
                var inner = ParseOr();
                _position++; // ')'
                return inner;
            }

            if (Peek == "(")
            {
                _position++;
                var args = new List<Func<Row, object?>>();
                while (Peek != ")")
                {
                    args.Add(ParseOr());
                    if (Peek == ",") _position++;
                }

                _position++;
                return Function(token, args);
            }

            if (token is "true" or "false" or "null" || token[0] == '\'' || char.IsDigit(token[0]) || token[0] == '-')
            {
                var literal = ParseLiteral(token);
                return _ => literal;
            }

            return row => Resolve(row, token);
        }

        private object? Resolve(Row row, string path)
        {
            var current = row;
            var currentSet = set;
            var segments = path.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!currentSet.Navs.TryGetValue(segments[i], out var nav)) return null;
                var target = projection.Sets[nav.TargetSet];
                var next = target.Rows.FirstOrDefault(t => nav.Match(current, t));
                if (next is null) return null;
                current = next;
                currentSet = target;
            }

            return current.GetValueOrDefault(segments[^1]);
        }

        private static Func<Row, object?> Function(string name, List<Func<Row, object?>> args) => name switch
        {
            "contains" => r => Str(args[0](r)).Contains(Str(args[1](r)), StringComparison.OrdinalIgnoreCase),
            "startswith" => r => Str(args[0](r)).StartsWith(Str(args[1](r)), StringComparison.OrdinalIgnoreCase),
            "endswith" => r => Str(args[0](r)).EndsWith(Str(args[1](r)), StringComparison.OrdinalIgnoreCase),
            "tolower" => r => Str(args[0](r)).ToLowerInvariant(),
            "toupper" => r => Str(args[0](r)).ToUpperInvariant(),
            "length" => r => (decimal)Str(args[0](r)).Length,
            _ => throw new InvalidOperationException($"Function '{name}' is not supported by the mock service."),
        };

        private static string Str(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        public static object? ParseLiteral(string token)
        {
            if (token == "null") return null;
            if (token == "true") return true;
            if (token == "false") return false;
            if (token.StartsWith('\'')) return token[1..^1].Replace("''", "'");
            if (decimal.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)) return number;
            return token;
        }

        public static int CompareValues(object? a, object? b)
        {
            if (a is null) return b is null ? 0 : -1;
            if (b is null) return 1;
            if (IsNumber(a) && IsNumber(b))
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }

            if (a is bool x && b is bool y) return x.CompareTo(y);
            return string.CompareOrdinal(Str(a), Str(b));
        }

        private static bool IsNumber(object value) => value is int or long or decimal or double;
    }
}
