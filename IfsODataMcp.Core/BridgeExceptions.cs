namespace IfsODataMcp.Core;

/// <summary>An error whose message is written here and safe to show to the agent.</summary>
public abstract class BridgeException : Exception
{
    protected BridgeException(string message, string code, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Stable, value-free category used in audit entries.</summary>
    public string Code { get; }
}

public sealed class PolicyViolationException(string message) : BridgeException(message, "policy_violation");

public sealed class QueryValidationException(string message) : BridgeException(message, "invalid_query");

public sealed class ReadOnlyViolationException(string message) : BridgeException(message, "read_only_violation");

public sealed class ResponseTooLargeException(string message) : BridgeException(message, "response_too_large");

public sealed class AuthenticationFailedException(string message) : BridgeException(message, "auth_failed");

public sealed class ConfigurationException(string message) : BridgeException(message, "configuration");

public sealed class UpstreamException(string message, int? statusCode = null, Exception? inner = null)
    : BridgeException(message, "upstream_error", inner)
{
    public int? StatusCode { get; } = statusCode;
}
