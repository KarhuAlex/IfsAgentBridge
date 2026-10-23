using System.Net;
using IfsODataMcp.Auth;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Logging;
using IfsODataMcp.Core.Query;
using IfsODataMcp.Core.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MockODataService;

namespace IfsODataMcp.Tests;

public class AuthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string StaticToken = "static-token-for-tests-0001";

    private WebApplicationFactory<Program> Protected(int? lifetimeSeconds = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Mock:RequireAuth", "true");
            b.UseSetting("Mock:StaticToken", StaticToken);
            if (lifetimeSeconds is not null) b.UseSetting("Mock:TokenLifetimeSeconds", lifetimeSeconds.ToString());
        });

    private static StubHandler EchoAuthorization(List<string?> seen) => StubHandler.Xml(MockData.ProcurementMetadata, request =>
    {
        seen.Add(request.Headers.Authorization?.ToString());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };
    });

    [Fact]
    public async Task No_auth_provides_no_token()
    {
        Assert.Null(await new NoAuthProvider().GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Static_bearer_reads_the_token_from_the_named_variable_and_registers_it_for_redaction()
    {
        var redactor = new SecretRedactor();
        var provider = new StaticBearerProvider("MY_VAR", redactor, name => name == "MY_VAR" ? "  " + StaticToken + " " : null);

        Assert.Equal(StaticToken, await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.DoesNotContain(StaticToken, redactor.Redact("leaked " + StaticToken));
    }

    [Fact]
    public void Static_bearer_without_a_token_fails_and_names_only_the_variable()
    {
        var ex = Assert.Throws<ConfigurationException>(() => new StaticBearerProvider("MY_VAR", new SecretRedactor(), _ => null));

        Assert.Contains("MY_VAR", ex.Message);
    }

    [Fact]
    public async Task Handler_adds_the_bearer_header_only_when_there_is_a_token()
    {
        var seen = new List<string?>();
        using var withToken = new HttpClient(new AuthenticatingHandler(new StaticBearerProvider("V", new SecretRedactor(), _ => StaticToken)) { InnerHandler = EchoAuthorization(seen) });
        using var without = new HttpClient(new AuthenticatingHandler(new NoAuthProvider()) { InnerHandler = EchoAuthorization(seen) });

        await withToken.GetAsync("http://localhost/x");
        await without.GetAsync("http://localhost/x");

        Assert.Equal(["Bearer " + StaticToken, null], seen);
    }

    [Fact]
    public async Task Handler_invalidates_the_token_when_the_api_answers_401()
    {
        var provider = new CountingProvider();
        using var client = new HttpClient(new AuthenticatingHandler(provider)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
        });

        await client.GetAsync("http://localhost/x");

        Assert.Equal(1, provider.Invalidations);
    }

    [Fact]
    public async Task Static_token_reaches_a_protected_service_through_the_full_pipeline()
    {
        await using var app = Protected();
        var redactor = new SecretRedactor();
        var provider = new StaticBearerProvider("V", redactor, _ => StaticToken);
        var gateway = GatewayWith(app, provider, redactor);

        var outcome = await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(Top: 2), CancellationToken.None);

        Assert.Equal(2, outcome.RowCount);
    }

    [Fact]
    public async Task Protected_service_rejects_a_missing_token_with_a_clear_error()
    {
        await using var app = Protected();
        var gateway = GatewayWith(app, new NoAuthProvider(), new SecretRedactor());

        var ex = await Assert.ThrowsAsync<UpstreamException>(() =>
            gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("authentication", ex.Message);
    }

    [Fact]
    public async Task Pkce_login_yields_a_token_the_protected_service_accepts()
    {
        await using var app = Protected();
        var redactor = new SecretRedactor();
        using var provider = Pkce(app, redactor);
        var gateway = GatewayWith(app, provider, redactor);

        var outcome = await gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(Top: 3), CancellationToken.None);

        Assert.Equal(3, outcome.RowCount);
        Assert.Equal(1, app.Services.GetRequiredService<MockIdp>().AuthorizationGrants);
    }

    [Fact]
    public async Task Pkce_token_is_cached_in_memory_between_calls()
    {
        await using var app = Protected();
        using var provider = Pkce(app, new SecretRedactor());

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, app.Services.GetRequiredService<MockIdp>().AuthorizationGrants);
    }

    [Fact]
    public async Task Pkce_concurrent_callers_trigger_a_single_login()
    {
        await using var app = Protected();
        using var provider = Pkce(app, new SecretRedactor());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAccessTokenAsync(CancellationToken.None)));

        Assert.Single(tokens.Distinct());
        Assert.Equal(1, app.Services.GetRequiredService<MockIdp>().AuthorizationGrants);
    }

    [Fact]
    public async Task Pkce_expired_token_is_refreshed_without_another_browser_login()
    {
        await using var app = Protected();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Pkce(app, new SecretRedactor(), clock);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        var idp = app.Services.GetRequiredService<MockIdp>();
        Assert.NotEqual(first, second);
        Assert.Equal(1, idp.AuthorizationGrants);
        Assert.Equal(1, idp.RefreshGrants);
    }

    [Fact]
    public async Task Pkce_invalidate_forces_a_refresh_on_the_next_call()
    {
        await using var app = Protected();
        using var provider = Pkce(app, new SecretRedactor());

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        provider.Invalidate();
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(1, app.Services.GetRequiredService<MockIdp>().RefreshGrants);
    }

    [Fact]
    public async Task Pkce_login_fails_cleanly_when_the_user_never_completes_sign_in()
    {
        await using var app = Protected();
        using var provider = new InteractivePkceProvider(
            Options(app, timeoutSeconds: 1), new SecretRedactor(), Quiet(),
            browserFactory: (port, path) => new LoopbackBrowser(port, path, _ => { /* user walks away */ }),
            backchannel: app.Server.CreateHandler());

        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Equal("auth_failed", ex.Code);
    }

    [Fact]
    public async Task Pkce_never_logs_or_leaks_tokens()
    {
        await using var app = Protected();
        var redactor = new SecretRedactor();
        var output = new StringWriter();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new RedactingLoggerProvider(output, redactor, LogLevel.Trace)));
        using var provider = new InteractivePkceProvider(
            Options(app), redactor, loggerFactory.CreateLogger("pkce"), (port, path) => new LoopbackBrowser(port, path, Visit(app)), app.Server.CreateHandler());

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.DoesNotContain(token!, output.ToString());
        Assert.DoesNotContain(token!, redactor.Redact("failed with " + token));
        Assert.Contains("memory only", output.ToString());
    }

    [Theory]
    [InlineData("", "client")]
    [InlineData("https://idp.example.test", "")]
    [InlineData("http://idp.example.test", "client")]
    [InlineData("not a url", "client")]
    public void Pkce_configuration_is_validated(string authority, string clientId)
    {
        Assert.Throws<ConfigurationException>(() =>
            new InteractivePkceProvider(new OidcOptions { Authority = authority, ClientId = clientId }, new SecretRedactor(), Quiet()));
    }

    [Fact]
    public async Task Loopback_browser_ignores_stray_requests_and_returns_the_callback_url()
    {
        var port = FreePort();
        var browser = new LoopbackBrowser(port, "/callback", url => Task.Run(async () =>
        {
            using var http = new HttpClient();
            using var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
            Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
            using var callback = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=abc&state=xyz<script>");
            var page = await callback.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script>", page);
        }));

        var result = await browser.InvokeAsync(new IdentityModel.OidcClient.Browser.BrowserOptions("https://idp.example.test/authorize", "http://127.0.0.1/"));

        Assert.Equal(IdentityModel.OidcClient.Browser.BrowserResultType.Success, result.ResultType);
        Assert.Contains("code=abc", result.Response);
    }

    [Fact]
    public async Task Loopback_browser_refuses_to_open_non_http_urls()
    {
        var opened = new List<Uri>();
        var browser = new LoopbackBrowser(FreePort(), "/callback", opened.Add);

        var result = await browser.InvokeAsync(new IdentityModel.OidcClient.Browser.BrowserOptions("file:///c:/windows/system32/calc.exe", "http://127.0.0.1/"));

        Assert.NotEqual(IdentityModel.OidcClient.Browser.BrowserResultType.Success, result.ResultType);
        Assert.Empty(opened);
    }

    private static Microsoft.Extensions.Logging.ILogger Quiet() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static OidcOptions Options(WebApplicationFactory<Program> app, int timeoutSeconds = 30) => new()
    {
        Authority = "http://localhost/idp",
        ClientId = MockIdp.ClientId,
        Scope = "openid offline_access",
        LoginTimeoutSeconds = timeoutSeconds,
    };

    private static Action<Uri> Visit(WebApplicationFactory<Program> app) => url => Task.Run(async () =>
    {
        using var idp = app.CreateClient();
        var noRedirect = new HttpClient(app.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        using var authorize = await noRedirect.GetAsync(url);
        var callback = authorize.Headers.Location!;
        using var real = new HttpClient();
        using var _ = await real.GetAsync(callback);
    });

    private InteractivePkceProvider Pkce(WebApplicationFactory<Program> app, SecretRedactor redactor, TimeProvider? clock = null) =>
        new(Options(app), redactor, Quiet(), (port, path) => new LoopbackBrowser(port, path, Visit(app)), app.Server.CreateHandler(), clock);

    private static IfsODataMcp.Core.Gateway.ODataGateway GatewayWith(WebApplicationFactory<Program> app, IAuthProvider auth, SecretRedactor redactor)
    {
        var options = TestSupport.Options();
        var urls = new ODataUrlBuilder(TestSupport.Endpoint());
        var pipeline = new IfsODataMcp.Core.Http.ReadOnlyGuardHandler(urls.BaseUri, false)
        {
            InnerHandler = new AuthenticatingHandler(auth) { InnerHandler = app.Server.CreateHandler() },
        };
        var http = new IfsODataMcp.Core.Http.ODataHttpClient(new HttpClient(pipeline), options, redactor);
        return new IfsODataMcp.Core.Gateway.ODataGateway(new IfsODataMcp.Core.Policy.AccessPolicy(options), urls, http, new IfsODataMcp.Core.Audit.NullAuditLog());
    }

    private sealed class CountingProvider : IAuthProvider
    {
        public int Invalidations { get; private set; }

        public string Name => "counting";

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("counting-token-value");

        public void Invalidate() => Invalidations++;
    }
}
