using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace IfsODataMcp.Core.Security;

/// <summary>Masks registered secrets and common token shapes in text that goes to logs or errors.</summary>
public sealed partial class SecretRedactor
{
    public const string Mask = "[REDACTED]";
    private const int MinSecretLength = 6;

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    public void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= MinSecretLength)
        {
            _secrets[secret] = 0;
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Longest first so a secret that contains another one is masked as a whole.
        foreach (var secret in _secrets.Keys.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = AuthSchemeRegex().Replace(text, "$1 " + Mask);
        text = JwtRegex().Replace(text, Mask);
        text = CredentialParameterRegex().Replace(text, "$1$2" + Mask);
        text = QueryCodeRegex().Replace(text, "$1code=" + Mask);
        return text;
    }

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]{6,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthSchemeRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"\b(access_token|refresh_token|id_token|client_secret|code_verifier|password|token)([""']?\s*[:=]\s*[""']?)[^&\s""',;]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialParameterRegex();

    [GeneratedRegex(@"([?&])code=[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex QueryCodeRegex();
}
