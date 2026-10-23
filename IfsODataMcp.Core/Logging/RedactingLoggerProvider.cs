using IfsODataMcp.Core.Security;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Core.Logging;

/// <summary>
/// Writes log lines to a <see cref="TextWriter"/> (stderr in the server, because stdout carries the MCP protocol)
/// after passing message and exception text through the <see cref="SecretRedactor"/>.
/// </summary>
public sealed class RedactingLoggerProvider(TextWriter output, SecretRedactor redactor, LogLevel minimumLevel) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var text = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category}: {redactor.Redact(text)}";
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }

    private sealed class RedactingLogger(RedactingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }

    private LogLevel MinimumLevel => minimumLevel;
}
