using System.Collections.Concurrent;
using MockODataService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(MockData.Create());
builder.Services.AddSingleton<RequestLog>();
builder.Services.AddSingleton<MockIdp>();

var app = builder.Build();
app.MapMockService();
app.Run();

namespace MockODataService
{
    using Row = Dictionary<string, object?>;

    /// <summary>Every request the mock received, so tests can assert on methods and paths.</summary>
    public sealed class RequestLog
    {
        private readonly ConcurrentQueue<(string Method, string PathAndQuery)> _entries = new();

        public void Add(string method, string pathAndQuery) => _entries.Enqueue((method, pathAndQuery));

        public IReadOnlyList<(string Method, string PathAndQuery)> Entries => _entries.ToArray();
    }

    public static class MockServiceEndpoints
    {
        public static void MapMockService(this WebApplication app)
        {
            var projections = app.Services.GetRequiredService<IReadOnlyDictionary<string, ProjectionDef>>();
            var log = app.Services.GetRequiredService<RequestLog>();
            var idp = app.Services.GetRequiredService<MockIdp>();
            var requireAuth = app.Configuration.GetValue<bool>("Mock:RequireAuth");
            var staticToken = app.Configuration["Mock:StaticToken"];

            idp.Map(app);

            app.MapGet("/", () => "Mock OData service (fictional data). Try /odata/ProcurementDemo/$metadata");

            app.Map("/odata/{projection}/{**rest}", async (HttpContext context, string projection, string? rest) =>
            {
                log.Add(context.Request.Method, context.Request.Path + context.Request.QueryString);

                if (context.Request.Method != HttpMethods.Get)
                {
                    return Error(405, "Only GET is supported by this read-only mock.");
                }

                if (requireAuth && !idp.IsAuthorized(context.Request.Headers.Authorization, staticToken))
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return Error(401, "A valid bearer token is required.");
                }

                if (!projections.TryGetValue(projection, out var def))
                {
                    return Error(404, $"Unknown projection '{projection}'.");
                }

                rest ??= "";
                if (rest == "$metadata")
                {
                    return Results.Text(def.MetadataXml, "application/xml");
                }

                var open = rest.IndexOf('(');
                var setName = open < 0 ? rest : rest[..open];
                if (!def.Sets.TryGetValue(setName, out var set))
                {
                    return Error(404, $"Unknown entity set '{setName}'.");
                }

                var options = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
                var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}/odata/{projection}";

                if (open >= 0)
                {
                    var row = MockQueryEngine.FindByKey(set, rest[(open + 1)..rest.LastIndexOf(')')]);
                    if (row is null)
                    {
                        return Error(404, "No entity with that key.");
                    }

                    options.TryGetValue("$select", out var select);
                    options.TryGetValue("$expand", out var expand);
                    var shaped = MockQueryEngine.Shape(def, set, row, select, expand);
                    shaped["@odata.context"] = $"{baseUrl}/$metadata#{setName}/$entity";
                    return Results.Json(shaped);
                }

                var (rows, hasMore) = MockQueryEngine.Query(def, set, options);
                var body = new Row
                {
                    ["@odata.context"] = $"{baseUrl}/$metadata#{setName}",
                    ["value"] = rows,
                };
                if (hasMore)
                {
                    body["@odata.nextLink"] = $"{baseUrl}/{setName}?$skip={rows.Count}";
                }

                return Results.Json(body);
            });
        }

        private static IResult Error(int status, string message) =>
            Results.Json(new { error = new { code = status.ToString(), message } }, statusCode: status);
    }
}

public partial class Program;
