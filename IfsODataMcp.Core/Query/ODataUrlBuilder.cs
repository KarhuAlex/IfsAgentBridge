using System.Globalization;
using System.Text;
using IfsODataMcp.Core.Configuration;

namespace IfsODataMcp.Core.Query;

public sealed class ODataUrlBuilder
{
    private const string SafeCharacters = "-._~'()*,:/";

    // Expand options are built here from validated names, so their separators stay readable.
    private const string ExpandSafeCharacters = "-._~'()*,:/$=;";

    // Key predicates sit in the path: keep '=' readable, but never let a '/' through.
    private const string KeySafeCharacters = "-._~'()*,:=";

    private readonly ODataEndpointOptions _options;

    public ODataUrlBuilder(ODataEndpointOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new ConfigurationException("Endpoint:BaseUrl must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ServiceRootTemplate) || !options.ServiceRootTemplate.Contains("{projection}", StringComparison.Ordinal))
        {
            throw new ConfigurationException("Endpoint:ServiceRootTemplate must be set and contain the {projection} placeholder.");
        }

        _options = options;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public Uri ServiceRoot(string projection)
    {
        var url = _options.ServiceRootTemplate
            .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{projection}", projection, StringComparison.Ordinal);
        return new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
    }

    public Uri Metadata(string projection) => new(ServiceRoot(projection), _options.MetadataPath);

    public Uri EntitySet(string projection, string entitySet, ValidatedQuery query)
    {
        var parameters = new List<string>();
        AddSelectAndExpand(parameters, query.Select, query.Expand);

        if (query.Filter is not null) parameters.Add("$filter=" + Escape(query.Filter));
        if (query.OrderBy.Count > 0)
        {
            parameters.Add("$orderby=" + string.Join(",", query.OrderBy.Select(o => o.Property + (o.Descending ? " desc" : " asc")).Select(item => Escape(item))));
        }

        parameters.Add("$top=" + query.Top);
        return Compose(projection, entitySet, "", parameters);
    }

    public Uri Entity(string projection, string entitySet, string keyPredicate, IReadOnlyList<string> select, IReadOnlyList<ExpandNode> expand)
    {
        var parameters = new List<string>();
        AddSelectAndExpand(parameters, select, expand);
        return Compose(projection, entitySet, Escape(keyPredicate, KeySafeCharacters), parameters);
    }

    private Uri Compose(string projection, string entitySet, string keyPart, List<string> parameters)
    {
        var url = new Uri(ServiceRoot(projection), entitySet).AbsoluteUri + keyPart;
        return new Uri(parameters.Count == 0 ? url : url + "?" + string.Join("&", parameters), UriKind.Absolute);
    }

    private static void AddSelectAndExpand(List<string> parameters, IReadOnlyList<string> select, IReadOnlyList<ExpandNode> expand)
    {
        if (select.Count > 0) parameters.Add("$select=" + string.Join(",", select));
        if (expand.Count > 0) parameters.Add("$expand=" + Escape(string.Join(",", expand.Select(RenderExpand)), ExpandSafeCharacters));
    }

    private static string RenderExpand(ExpandNode node)
    {
        var options = new List<string>();
        if (node.Select.Count > 0) options.Add("$select=" + string.Join(",", node.Select));
        if (node.Children.Count > 0) options.Add("$expand=" + string.Join(",", node.Children.Select(RenderExpand)));
        return options.Count == 0 ? node.Navigation : $"{node.Navigation}({string.Join(";", options)})";
    }

    internal static string Escape(string value, string safe = SafeCharacters)
    {
        var builder = new StringBuilder(value.Length + 16);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || safe.Contains((char)rune.Value, StringComparison.Ordinal)))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var count = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < count; i++) builder.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
