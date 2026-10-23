using IfsODataMcp.Auth;
using IfsODataMcp.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Server;

/// <summary>
/// Starts the interactive sign-in in the background when the server starts, so the token is usually
/// ready before the first tool call. A failure here is logged; the next tool call will try again.
/// </summary>
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
