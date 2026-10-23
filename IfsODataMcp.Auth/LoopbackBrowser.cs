using System.Diagnostics;
using System.Net;
using System.Text;
using IdentityModel.OidcClient.Browser;

namespace IfsODataMcp.Auth;

/// <summary>Opens the system browser and receives the redirect on 127.0.0.1, answering with a fixed page.</summary>
public sealed class LoopbackBrowser : IBrowser
{
    private const string DonePage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Signed in</title></head>" +
        "<body style=\"font-family:sans-serif\"><p>Sign-in finished. You can close this tab and return to your application.</p></body></html>";

    private readonly int _port;
    private readonly string _path;
    private readonly Action<Uri> _openBrowser;

    public LoopbackBrowser(int port, string path, Action<Uri>? openBrowser = null)
    {
        _port = port;
        _path = path;
        _openBrowser = openBrowser ?? OpenSystemBrowser;
    }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return Failure($"The loopback listener could not start on port {_port}: {ex.Message}");
        }

        if (!Uri.TryCreate(options.StartUrl, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
        {
            return Failure("The authorization URL is not an http(s) URL.");
        }

        _openBrowser(start);

        using var stop = cancellationToken.Register(listener.Stop);
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                if (context.Request.Url is { } url && string.Equals(url.AbsolutePath, _path, StringComparison.Ordinal)
                    && context.Request.HttpMethod == "GET")
                {
                    await RespondAsync(context, 200, DonePage).ConfigureAwait(false);
                    return new BrowserResult { ResultType = BrowserResultType.Success, Response = url.AbsoluteUri };
                }

                // Favicon requests and stray probes must not end the login.
                await RespondAsync(context, 404, "").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Sign-in was not completed in time." };
        }
    }

    private static BrowserResult Failure(string error) =>
        new() { ResultType = BrowserResultType.UnknownError, Error = error };

    private static async Task RespondAsync(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static void OpenSystemBrowser(Uri url)
    {
        // Only http(s) URLs reach this point; the shell never sees anything else.
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
