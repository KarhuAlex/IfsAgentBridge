using IfsODataMcp.Auth;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Audit;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Gateway;
using IfsODataMcp.Core.Http;
using IfsODataMcp.Core.Policy;
using IfsODataMcp.Core.Query;
using IfsODataMcp.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Server;

public static class BridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the tools need. <paramref name="primaryHandler"/> replaces the real network handler
    /// (tests pass an in-memory server); the read-only guard and authentication handlers are always in front of it.
    /// </summary>
    public static IServiceCollection AddIfsAgentBridge(
        this IServiceCollection services,
        IConfiguration configuration,
        SecretRedactor? redactor = null,
        HttpMessageHandler? primaryHandler = null)
    {
        var endpoint = configuration.GetSection("Endpoint").Get<ODataEndpointOptions>() ?? new ODataEndpointOptions();
        var policy = configuration.GetSection("Policy").Get<PolicyOptions>() ?? new PolicyOptions();
        var audit = configuration.GetSection("Audit").Get<AuditOptions>() ?? new AuditOptions();
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        redactor ??= new SecretRedactor();
        services.AddSingleton(redactor);
        services.AddSingleton(endpoint);
        services.AddSingleton(policy);
        services.AddSingleton(auth);
        services.AddSingleton(new AccessPolicy(policy));
        services.AddSingleton(new ODataUrlBuilder(endpoint));

        services.AddSingleton<IAuthProvider>(sp => auth.Mode switch
        {
            AuthMode.None => new NoAuthProvider(),
            AuthMode.StaticBearer => new StaticBearerProvider(auth.StaticBearerEnvVar, sp.GetRequiredService<SecretRedactor>()),
            AuthMode.InteractivePkce => new InteractivePkceProvider(
                auth.Oidc, sp.GetRequiredService<SecretRedactor>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<InteractivePkceProvider>()),
            _ => throw new ConfigurationException($"Unknown Auth:Mode '{auth.Mode}'."),
        });

        services.AddSingleton<IAuditLog>(_ => audit.Enabled ? new FileAuditLog(audit.Path) : new NullAuditLog());

        services.AddSingleton(sp =>
        {
            var urls = sp.GetRequiredService<ODataUrlBuilder>();

            // Pipeline, outermost first: read-only guard -> authentication -> network.
            // Redirects are never followed, so a response cannot move the token to another host.
            var network = primaryHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
            var authenticating = new AuthenticatingHandler(sp.GetRequiredService<IAuthProvider>()) { InnerHandler = network };
            var guard = new ReadOnlyGuardHandler(urls.BaseUri, endpoint.AllowInsecureHttp) { InnerHandler = authenticating };
            return new ODataHttpClient(new HttpClient(guard) { Timeout = Timeout.InfiniteTimeSpan }, policy, sp.GetRequiredService<SecretRedactor>());
        });

        services.AddSingleton<ODataGateway>();
        services.AddSingleton<ToolRunner>();
        services.AddHostedService<AuthWarmupService>();
        return services;
    }
}
