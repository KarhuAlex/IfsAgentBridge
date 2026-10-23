using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IfsODataMcp.Core.Audit;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Http;
using IfsODataMcp.Core.Metadata;
using IfsODataMcp.Core.Policy;
using IfsODataMcp.Core.Query;

namespace IfsODataMcp.Core.Gateway;

public sealed record ProjectionSummary(string Name, string? Description, IReadOnlyList<string>? EntitySets);

public sealed record NavigationDescription(string Name, string TargetType, IReadOnlyList<string> TargetEntitySets, bool IsCollection);

public sealed record EntitySetDescription(
    string Projection,
    string EntitySet,
    string EntityType,
    IReadOnlyList<PropertyInfo> Keys,
    IReadOnlyList<PropertyInfo> Properties,
    IReadOnlyList<NavigationDescription> NavigationProperties);

/// <param name="Data">An array of entities for a query, a single object for get_entity.</param>
public sealed record QueryOutcome(
    string Projection,
    string EntitySet,
    JsonNode Data,
    int RowCount,
    bool HasMore,
    IReadOnlyList<string> Warnings);

public sealed record DryRunOutcome(string Url, string Method, IReadOnlyList<string> Warnings);

/// <summary>
/// Orchestrates one tool call: policy check, metadata lookup, validation, URL building, the GET, sanitizing
/// and the audit entry. Tools are thin wrappers around this class.
/// </summary>
public sealed class ODataGateway
{
    private readonly AccessPolicy _policy;
    private readonly ODataUrlBuilder _urls;
    private readonly ODataHttpClient _http;
    private readonly IAuditLog _audit;
    private readonly QueryValidator _validator;
    private readonly ConcurrentDictionary<string, Lazy<Task<ServiceMetadata>>> _metadata = new(StringComparer.Ordinal);

    public ODataGateway(AccessPolicy policy, ODataUrlBuilder urls, ODataHttpClient http, IAuditLog audit)
    {
        _policy = policy;
        _urls = urls;
        _http = http;
        _audit = audit;
        _validator = new QueryValidator(policy);
    }

    public IReadOnlyList<ProjectionSummary> ListConfiguredProjections() =>
        _policy.Projections
            .Select(p => new ProjectionSummary(p.Name, p.Description, p.EntitySets is { Count: > 0 } ? p.EntitySets : null))
            .ToList();

    /// <summary>Lists projections; with <paramref name="includeEntitySets"/> also the allowed entity sets taken from $metadata.</summary>
    public Task<IReadOnlyList<ProjectionSummary>> ListProjectionsAsync(bool includeEntitySets, CancellationToken ct) =>
        AuditedAsync("list_projections", null, null, includeEntitySets ? ["includeEntitySets"] : [], async () =>
        {
            var configured = ListConfiguredProjections();
            if (!includeEntitySets)
            {
                return ((IReadOnlyList<ProjectionSummary>)configured, (int?)configured.Count);
            }

            var result = new List<ProjectionSummary>();
            foreach (var projection in configured)
            {
                var rule = _policy.EnsureProjectionAllowed(projection.Name);
                var metadata = await GetMetadataAsync(projection.Name, ct).ConfigureAwait(false);
                var sets = metadata.EntitySets.Select(s => s.Name).Where(n => _policy.IsEntitySetAllowed(rule, n)).Order().ToList();
                result.Add(projection with { EntitySets = sets });
            }

            return (result, configured.Count);
        });

    public Task<EntitySetDescription> DescribeEntitySetAsync(string projection, string entitySet, CancellationToken ct) =>
        AuditedAsync("describe_entity_set", projection, entitySet, [], async () =>
        {
            var (rule, metadata, set) = await ResolveAsync(projection, entitySet, ct).ConfigureAwait(false);
            var type = set.EntityType;

            var properties = _policy.AllowedProperties(type).ToList();
            var navigations = type.Navigations
                .Where(n => _policy.IsNavigationAllowed(rule, metadata, n))
                .Select(n => new NavigationDescription(
                    n.Name,
                    n.TargetType,
                    metadata.FindType(n.TargetType)!.EntitySetNames.Where(s => _policy.IsEntitySetAllowed(rule, s)).ToList(),
                    n.IsCollection))
                .ToList();

            var description = new EntitySetDescription(
                projection, entitySet, type.Name, properties.Where(p => p.IsKey).ToList(), properties, navigations);
            return (description, (int?)null);
        });

    public Task<QueryOutcome> QueryAsync(string projection, string entitySet, ODataQueryRequest request, CancellationToken ct) =>
        AuditedAsync("query_entity_set", projection, entitySet, request.ParameterNames(), async () =>
        {
            var (rule, metadata, set) = await ResolveAsync(projection, entitySet, ct).ConfigureAwait(false);
            var query = _validator.Validate(rule, metadata, set, request);
            var url = _urls.EntitySet(projection, entitySet, query);

            var root = ParseJson(await _http.GetStringAsync(url, "application/json", ct).ConfigureAwait(false));
            var sanitizer = new ResultSanitizer(_policy, metadata);
            var warnings = query.Warnings.ToList();

            var rows = new JsonArray();
            var received = 0;
            if (root["value"] is JsonArray value)
            {
                received = value.Count;
                foreach (var item in value.OfType<JsonObject>().Take(query.Top))
                {
                    rows.Add(sanitizer.Sanitize(item.DeepClone().AsObject(), set.EntityType));
                }
            }

            var hasMore = root["@odata.nextLink"] is not null || received > rows.Count;
            if (received > query.Top)
            {
                warnings.Add($"The server returned more than top={query.Top} rows; the extra rows were dropped.");
            }

            var outcome = new QueryOutcome(projection, entitySet, rows, rows.Count, hasMore, warnings);
            return (outcome, (int?)rows.Count);
        });

    public Task<QueryOutcome> GetEntityAsync(
        string projection,
        string entitySet,
        IReadOnlyDictionary<string, JsonElement> keys,
        ODataQueryRequest options,
        CancellationToken ct) =>
        AuditedAsync("get_entity", projection, entitySet, options.ParameterNames().Append("keys").ToList(), async () =>
        {
            var (rule, metadata, set) = await ResolveAsync(projection, entitySet, ct).ConfigureAwait(false);
            var (url, warnings) = BuildEntityUrl(rule, metadata, set, keys, options);

            var root = ParseJson(await _http.GetStringAsync(url, "application/json", ct).ConfigureAwait(false));
            var entity = new ResultSanitizer(_policy, metadata).Sanitize(root, set.EntityType);
            return (new QueryOutcome(projection, entitySet, entity, 1, false, warnings), (int?)1);
        });

    /// <summary>Validates exactly like a real call and returns the URL without requesting any data.</summary>
    public Task<DryRunOutcome> BuildUrlAsync(
        string projection,
        string entitySet,
        ODataQueryRequest request,
        IReadOnlyDictionary<string, JsonElement>? keys,
        CancellationToken ct) =>
        AuditedAsync("build_odata_url", projection, entitySet, request.ParameterNames().Concat(keys is null ? [] : ["keys"]).ToList(), async () =>
        {
            var (rule, metadata, set) = await ResolveAsync(projection, entitySet, ct).ConfigureAwait(false);
            if (keys is { Count: > 0 })
            {
                var (entityUrl, entityWarnings) = BuildEntityUrl(rule, metadata, set, keys, request);
                return (new DryRunOutcome(entityUrl.AbsoluteUri, "GET", entityWarnings), (int?)null);
            }

            var query = _validator.Validate(rule, metadata, set, request);
            return (new DryRunOutcome(_urls.EntitySet(projection, entitySet, query).AbsoluteUri, "GET", query.Warnings), (int?)null);
        });

    private (Uri Url, IReadOnlyList<string> Warnings) BuildEntityUrl(
        ProjectionRule rule, ServiceMetadata metadata, EntitySetInfo set, IReadOnlyDictionary<string, JsonElement> keys, ODataQueryRequest options)
    {
        var predicate = KeyPredicateBuilder.Build(set.EntityType, keys);
        var query = _validator.Validate(rule, metadata, set, options with { Filter = null, OrderBy = null, Top = null });
        return (_urls.Entity(rule.Name, set.Name, predicate, query.Select, query.Expand), query.Warnings);
    }

    private async Task<(ProjectionRule Rule, ServiceMetadata Metadata, EntitySetInfo Set)> ResolveAsync(
        string projection, string entitySet, CancellationToken ct)
    {
        var rule = _policy.EnsureProjectionAllowed(projection);
        _policy.EnsureEntitySetAllowed(rule, entitySet);

        var metadata = await GetMetadataAsync(projection, ct).ConfigureAwait(false);
        var set = metadata.FindEntitySet(entitySet)
            ?? throw new QueryValidationException($"Entity set '{entitySet}' does not exist in projection '{projection}'.");
        return (rule, metadata, set);
    }

    private async Task<ServiceMetadata> GetMetadataAsync(string projection, CancellationToken ct)
    {
        var lazy = _metadata.GetOrAdd(projection, name => new Lazy<Task<ServiceMetadata>>(() => LoadMetadataAsync(name)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not cache failures; the next call retries.
            _metadata.TryRemove(new KeyValuePair<string, Lazy<Task<ServiceMetadata>>>(projection, lazy));
            throw;
        }
    }

    private async Task<ServiceMetadata> LoadMetadataAsync(string projection)
    {
        var xml = await _http.GetStringAsync(_urls.Metadata(projection), "application/xml", CancellationToken.None).ConfigureAwait(false);
        return MetadataParser.Parse(xml);
    }

    private static JsonObject ParseJson(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new UpstreamException("The API returned JSON that is not an object.");
        }
        catch (JsonException)
        {
            throw new UpstreamException("The API returned a response that is not valid JSON.");
        }
    }

    private async Task<T> AuditedAsync<T>(
        string tool, string? projection, string? entitySet, IReadOnlyList<string> parameterNames, Func<Task<(T Result, int? Rows)>> work)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "ok";
        int? rows = null;
        try
        {
            var (result, rowCount) = await work().ConfigureAwait(false);
            rows = rowCount;
            return result;
        }
        catch (BridgeException ex)
        {
            outcome = ex.Code;
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, tool, projection, entitySet, parameterNames, rows, stopwatch.ElapsedMilliseconds, outcome));
        }
    }
}
