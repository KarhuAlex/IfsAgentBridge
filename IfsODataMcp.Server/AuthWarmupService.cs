using IfsODataMcp.Auth;
using IfsODataMcp.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Server;

/// <summary>Starts the sign-in in the background so the token is usually ready for the first tool call.</summary>
public sealed class AuthWarmupService(IAuthProvider auth, AuthOptions options, ILogger<AuthWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Mode != AuthMode.InteractivePkce || !options.LoginAtStartup)
        {
            return;
        }

        try
        {
            await auth.GetAccessTokenAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is BridgeException or HttpRequestException)
        {
            logger.LogWarning("Sign-in at startup did not complete: {Message}", ex.Message);
        }
    }
}
