using System.Text;
using System.Text.RegularExpressions;

namespace LocalLlmConsole.Services;

public sealed record GuardLogLine(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string ModelId,
    string Level,
    string Text);

public sealed record GuardLogFilter(
    string? SearchText = null,
    bool ErrorsOnly = false,
    bool CudaOrOomOnly = false,
    string? ModelId = null,
    string? SessionId = null);

/// <summary>
/// Phase 4B-3 Module G: Redacted Session Log Service.
/// Strictly isolates logs from external Codex/Hermes files and redacts all API keys / Bearer tokens / Guard secrets.
/// Supports search, ERROR filter, CUDA/OOM filter, model filter, session filter, historical retention, and export.
/// </summary>
public sealed partial class GuardSessionLogService
{
    private readonly object _sync = new();
    private readonly List<GuardLogLine> _entries = [];
    private readonly string _logDirectory;

    [GeneratedRegex(@"(?i)(Bearer\s+[A-Za-z0-9._\-~+/=]+|guard-(?:internal|admin)-[A-Za-z0-9_\-]{6,}|agent-inf-[A-Za-z0-9_\-]{6,}|sk-[A-Za-z0-9_\-]{8,}|hf_[A-Za-z0-9]{10,}|(?:x-api-key|authorization)\s*[:=]\s*[^\s,;]+)", RegexOptions.Compiled)]
    private static partial Regex SecretPattern();

    public GuardSessionLogService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public bool IsPaused { get; set; }

    public static string RedactSecrets(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : SecretPattern().Replace(text, "***REDACTED***");

    public static bool IsIsolatedFromExternalLogs(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return !name.Contains("codex", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("hermes", StringComparison.OrdinalIgnoreCase);
    }

    public void Append(string sessionId, string modelId, string level, string rawText)
    {
        var sanitized = RedactSecrets(rawText);
        var entry = new GuardLogLine(DateTimeOffset.UtcNow, sessionId, modelId, level, sanitized);
        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > 5000)
                _entries.RemoveRange(0, _entries.Count - 5000);
        }

        var safeSession = ModelGatewayRouteId.SafeSegment(sessionId);
        var sessionFile = Path.Combine(_logDirectory, $"guard-session-{safeSession}.log");
        File.AppendAllText(sessionFile, $"[{entry.TimestampUtc:O}] [{entry.Level}] [{entry.ModelId}] {entry.Text}{Environment.NewLine}", Encoding.UTF8);
    }

    public IReadOnlyList<GuardLogLine> Query(GuardLogFilter? filter = null)
    {
        lock (_sync)
        {
            IEnumerable<GuardLogLine> seq = _entries;
            if (filter is not null)
            {
                if (!string.IsNullOrWhiteSpace(filter.SearchText))
                    seq = seq.Where(e => e.Text.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase)
                        || e.ModelId.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase));
                if (filter.ErrorsOnly)
                    seq = seq.Where(e => e.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                        || e.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase)
                        || e.Text.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || e.Text.Contains("failed", StringComparison.OrdinalIgnoreCase));
                if (filter.CudaOrOomOnly)
                    seq = seq.Where(e => e.Text.Contains("cuda", StringComparison.OrdinalIgnoreCase)
                        || e.Text.Contains("oom", StringComparison.OrdinalIgnoreCase)
                        || e.Text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                        || e.Text.Contains("vram", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(filter.ModelId))
                    seq = seq.Where(e => e.ModelId.Equals(filter.ModelId, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(filter.SessionId))
                    seq = seq.Where(e => e.SessionId.Equals(filter.SessionId, StringComparison.OrdinalIgnoreCase));
            }
            return seq.ToArray();
        }
    }

    public IReadOnlyList<string> ListHistoricalSessions()
    {
        if (!Directory.Exists(_logDirectory))
            return [];
        return Directory.EnumerateFiles(_logDirectory, "guard-session-*.log")
            .Where(IsIsolatedFromExternalLogs)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();
    }

    public string ExportFilteredLogs(string outputFilePath, GuardLogFilter? filter = null)
    {
        var lines = Query(filter);
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.AppendLine($"[{l.TimestampUtc:O}] [{l.SessionId}] [{l.ModelId}] [{l.Level}] {RedactSecrets(l.Text)}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath) ?? _logDirectory);
        File.WriteAllText(outputFilePath, sb.ToString(), Encoding.UTF8);
        return outputFilePath;
    }
}
