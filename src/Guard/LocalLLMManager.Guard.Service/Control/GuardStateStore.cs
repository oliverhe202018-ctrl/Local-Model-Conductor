using System.Text;
using System.Text.Json;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Control;

/// <summary>
/// Persists Guard operational state atomically (for clean restart recovery) and writes redacted JSONL audit logs.
/// </summary>
public sealed class GuardStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly Func<string?[]> _secretsProvider;

    public string StateFilePath { get; }
    public string AuditLogPath { get; }

    public GuardStateStore(string stateDirectory, Func<string?[]>? secretsProvider = null)
    {
        Directory.CreateDirectory(stateDirectory);
        GuardCredentialStore.ApplyStrictDirectoryAcl(stateDirectory);
        StateFilePath = Path.Combine(stateDirectory, "guard-state.json");
        AuditLogPath = Path.Combine(stateDirectory, "guard-audit.jsonl");
        _secretsProvider = secretsProvider ?? (() => []);
    }

    public GuardPersistedState LoadState()
    {
        lock (_sync)
        {
            if (!File.Exists(StateFilePath))
                return new GuardPersistedState();

            try
            {
                var json = File.ReadAllText(StateFilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<GuardPersistedState>(json, JsonOptions) ?? new GuardPersistedState();
            }
            catch
            {
                // Corrupt state fails safe to READ_ONLY_OBSERVATION / FAULT_LOCKED awareness
                return new GuardPersistedState
                {
                    OperationalState = GuardOperationalState.FAULT_LOCKED,
                    FaultLockReason = "Persisted guard-state.json was unreadable on startup; locked until operator verifies."
                };
            }
        }
    }

    public void SaveState(GuardPersistedState state)
    {
        lock (_sync)
        {
            // Keep idempotency cache bounded to latest 100 entries
            var trimmed = state;
            if (state.RecentIdempotentResponses.Count > 100)
            {
                var keep = state.RecentIdempotentResponses
                    .OrderByDescending(kv => kv.Value.CompletedAtUtc)
                    .Take(100)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                trimmed = state with { RecentIdempotentResponses = keep };
            }

            var secrets = _secretsProvider();
            var rawJson = JsonSerializer.Serialize(trimmed, JsonOptions);
            var safeJson = LogRedactor.Redact(rawJson, secrets);

            var tmp = StateFilePath + ".tmp";
            File.WriteAllText(tmp, safeJson, Encoding.UTF8);
            GuardCredentialStore.ApplyStrictFileAcl(tmp);
            File.Move(tmp, StateFilePath, overwrite: true);
            GuardCredentialStore.ApplyStrictFileAcl(StateFilePath);
        }
    }

    public void AppendAudit(GuardAuditEntry entry)
    {
        lock (_sync)
        {
            var secrets = _secretsProvider();
            var redactedEntry = entry with
            {
                Summary = LogRedactor.Redact(entry.Summary, secrets),
                CallerId = LogRedactor.Redact(entry.CallerId, secrets)
            };
            var line = JsonSerializer.Serialize(redactedEntry, CompactJsonOptions);
            File.AppendAllText(AuditLogPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public IReadOnlyList<GuardAuditEntry> ReadRecentAudit(int maxCount = 50)
    {
        lock (_sync)
        {
            if (!File.Exists(AuditLogPath))
                return [];

            var lines = File.ReadAllLines(AuditLogPath, Encoding.UTF8);
            var list = new List<GuardAuditEntry>();
            foreach (var line in lines.TakeLast(Math.Clamp(maxCount, 1, 500)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<GuardAuditEntry>(line, CompactJsonOptions);
                    if (item is not null) list.Add(item);
                }
                catch
                {
                    // Skip malformed line
                }
            }
            return list;
        }
    }
}
