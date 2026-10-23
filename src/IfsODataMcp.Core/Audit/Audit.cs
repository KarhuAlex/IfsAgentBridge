using System.Text.Json;
using System.Text.Json.Serialization;

namespace IfsODataMcp.Core.Audit;

/// <summary>
/// One line of the audit log. By construction it has no field that can hold a data value or a token:
/// only names, counts and timings.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Tool,
    string? Projection,
    string? EntitySet,
    IReadOnlyList<string> ParameterNames,
    int? RowCount,
    long DurationMs,
    string Outcome);

public interface IAuditLog
{
    void Write(AuditEntry entry);
}

public sealed class NullAuditLog : IAuditLog
{
    public void Write(AuditEntry entry)
    {
    }
}

/// <summary>Appends JSON lines to a local file. A failure to write never fails the request.</summary>
public sealed class FileAuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public FileAuditLog(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string FilePath => _path;

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IfsAgentBridge", "audit.jsonl");

    public void Write(AuditEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, Json);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Auditing is best effort; the error text is intentionally not forwarded anywhere.
        }
    }
}
