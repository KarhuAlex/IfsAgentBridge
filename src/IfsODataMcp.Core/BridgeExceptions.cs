namespace IfsODataMcp.Core;

/// <summary>
/// Base type for errors whose message is written by this project and is safe to show to an agent.
/// Anything that is not a <see cref="BridgeException"/> is reported as a generic failure.
/// </summary>
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

/// <summary>The request touches something the configured policy does not allow.</summary>
public sealed class PolicyViolationException(string message) : BridgeException(message, "policy_violation");

/// <summary>The request is malformed or does not match the service metadata.</summary>
public sealed class QueryValidationException(string message) : BridgeException(message, "invalid_query");

/// <summary>Something other than HTTP GET was about to be sent to the target API.</summary>
public sealed class ReadOnlyViolationException(string message) : BridgeException(message, "read_only_violation");

public sealed class ResponseTooLargeException(string message) : BridgeException(message, "response_too_large");

public sealed class ConfigurationException(string message) : BridgeException(message, "configuration");

/// <summary>The target API answered with an error, or could not be reached.</summary>
public sealed class UpstreamException(string message, int? statusCode = null, Exception? inner = null)
    : BridgeException(message, "upstream_error", inner)
{
    public int? StatusCode { get; } = statusCode;
}
