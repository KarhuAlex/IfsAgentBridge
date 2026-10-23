using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IfsODataMcp.Core.Configuration;
using IfsODataMcp.Core.Security;

namespace IfsODataMcp.Core.Http;

/// <summary>
/// The only component that talks to the target API. It exposes a single operation, GET, and enforces
/// the timeout and response-size limits.
/// </summary>
public sealed class ODataHttpClient(HttpClient http, PolicyOptions limits, SecretRedactor redactor)
{
    public async Task<string> GetStringAsync(Uri uri, string accept, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("OData-MaxVersion", "4.0");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await ToUpstreamExceptionAsync(response, timeout.Token).ConfigureAwait(false);
            }

            if (response.Content.Headers.ContentLength > limits.MaxResponseBytes)
            {
                throw TooLarge();
            }

            return await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamException($"The request timed out after {limits.RequestTimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            // The exception text can echo URLs; run it through the redactor and drop the original as inner exception.
            throw new UpstreamException($"The request failed: {redactor.Redact(ex.Message)}", (int?)ex.StatusCode);
        }
    }

    private async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limits.MaxResponseBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private ResponseTooLargeException TooLarge() =>
        new($"The response is larger than the limit of {limits.MaxResponseBytes} bytes. Use a smaller top, a narrower select or a stricter filter.");

    private async Task<UpstreamException> ToUpstreamExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var detail = "";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body.Length > 8192 ? body[..8192] : body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString() ?? "";
                detail = ": " + redactor.Redact(text.Length > 200 ? text[..200] + "..." : text);
            }
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or HttpRequestException)
        {
            // No usable error body; the status code alone has to do.
        }

        var hint = status switch
        {
            401 => " (authentication failed or token expired)",
            403 => " (the account is not permitted to read this)",
            404 => " (not found)",
            _ => "",
        };

        return new UpstreamException($"The API answered HTTP {status}{hint}{detail}", status);
    }
}
