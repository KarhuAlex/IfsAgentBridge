using System.Globalization;
using System.Text.Json;
using IfsODataMcp.Core.Metadata;

namespace IfsODataMcp.Core.Query;

/// <summary>
/// Renders a key predicate such as <c>(OrderNo='A1',LineNo=2)</c> from typed values.
/// Values are formatted according to the key property's Edm type, so a value can never change the URL structure.
/// </summary>
public static class KeyPredicateBuilder
{
    public static string Build(EntityTypeInfo type, IReadOnlyDictionary<string, JsonElement> keys)
    {
        var keyProperties = type.Keys.ToList();
        if (keyProperties.Count == 0)
        {
            throw new QueryValidationException($"{type.Name} has no key in the metadata.");
        }

        var unknown = keys.Keys.Where(k => keyProperties.All(p => p.Name != k)).ToList();
        if (unknown.Count > 0)
        {
            throw new QueryValidationException(
                $"'{unknown[0]}' is not a key property of {type.Name}. Keys: {string.Join(", ", keyProperties.Select(p => p.Name))}.");
        }

        var parts = new List<string>();
        foreach (var property in keyProperties)
        {
            if (!keys.TryGetValue(property.Name, out var value))
            {
                throw new QueryValidationException(
                    $"Missing key property '{property.Name}'. Keys: {string.Join(", ", keyProperties.Select(p => p.Name))}.");
            }

            var literal = Literal(property, value);
            parts.Add(keyProperties.Count == 1 ? literal : $"{property.Name}={literal}");
        }

        return "(" + string.Join(",", parts) + ")";
    }

    private static string Literal(PropertyInfo property, JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new QueryValidationException($"Key '{property.Name}' must be a string, number or boolean."),
        };

        switch (property.Type)
        {
            case "Edm.String":
                return "'" + text.Replace("'", "''") + "'";
            case "Edm.Int16" or "Edm.Int32" or "Edm.Int64" or "Edm.Byte" or "Edm.SByte":
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    throw Bad(property);
                return integer.ToString(CultureInfo.InvariantCulture);
            case "Edm.Decimal" or "Edm.Double" or "Edm.Single":
                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    throw Bad(property);
                return number.ToString(CultureInfo.InvariantCulture);
            case "Edm.Boolean":
                if (!bool.TryParse(text, out var flag)) throw Bad(property);
                return flag ? "true" : "false";
            case "Edm.Guid":
                if (!Guid.TryParse(text, out var guid)) throw Bad(property);
                return guid.ToString("D");
            case "Edm.Date":
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw Bad(property);
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "Edm.DateTimeOffset":
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp))
                    throw Bad(property);
                return stamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            default:
                throw new QueryValidationException($"Key '{property.Name}' has unsupported type {property.Type}.");
        }
    }

    private static QueryValidationException Bad(PropertyInfo property) =>
        new($"Key '{property.Name}' is not a valid {property.Type} value.");
}
