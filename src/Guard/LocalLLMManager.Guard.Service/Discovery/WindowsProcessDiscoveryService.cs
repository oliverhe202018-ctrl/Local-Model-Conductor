using System.Management;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Discovery;

public sealed class ProcessDiscoveryOptions
{
    /// <summary>Executable image names recognized as local LLM inference servers.</summary>
    public IReadOnlyList<string> TargetImageNames { get; init; } = ["llama-server.exe"];

    /// <summary>
    /// Optional port scope filter for isolated Phase 4A spike testing (e.g. 19250-19260).
    /// When null, scans ALL matching model servers across the entire host (production mode).
    /// </summary>
    public int? PortRangeMin { get; init; }
    public int? PortRangeMax { get; init; }

    /// <summary>Known Guard-managed llama-swap gateway PID.</summary>
    public Func<int?>? ManagedGatewayPidProvider { get; init; }

    /// <summary>Optional known secrets to strip from discovered command lines.</summary>
    public Func<string?[]>? KnownSecretsProvider { get; init; }
}

public interface IExternalProcessDiscoveryService
{
    Task<IReadOnlyList<DiscoveredModelProcess>> DiscoverAsync(
        HostHardwareTelemetry? telemetry = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Upgraded read-only OS discovery service inheriting WindowsProcessInspector + WindowsTcpPortTable.
///
/// STRICT READ-ONLY GUARANTEE:
/// Never calls Process.Kill, OpenProcess(TERMINATE), AttachConsole, or injection APIs.
/// Uses Win32_Process WMI + GetExtendedTcpTable + nvidia-smi compute-app map solely for observation.
///
/// Classifies every discovered model process into:
/// - MANAGED_BY_LLAMA_SWAP
/// - EXTERNAL_CODEX_OWNED
/// - EXTERNAL_UNMANAGED
/// - UNKNOWN_OWNER (fails closed!)
/// </summary>
public sealed class WindowsProcessDiscoveryService : IExternalProcessDiscoveryService
{
    private readonly ProcessDiscoveryOptions _options;

    public WindowsProcessDiscoveryService(ProcessDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<DiscoveredModelProcess>> DiscoverAsync(
        HostHardwareTelemetry? telemetry = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => DiscoverCore(telemetry), cancellationToken);

    private IReadOnlyList<DiscoveredModelProcess> DiscoverCore(HostHardwareTelemetry? telemetry)
    {
        var listeners = WindowsTcpPortTable.GetAllListeningTcpPorts();
        var portsByPid = listeners
            .GroupBy(l => l.ProcessId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(x => x.Port).Distinct().OrderBy(p => p).ToArray());

        var gpuByPid = (telemetry?.GpuProcesses ?? [])
            .GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => g.First().UsedVramMiB);

        var allWmi = QueryAllProcessesWmi();
        var targetSet = new HashSet<string>(_options.TargetImageNames, StringComparer.OrdinalIgnoreCase);
        var gatewayPid = _options.ManagedGatewayPidProvider?.Invoke();
        var secrets = _options.KnownSecretsProvider?.Invoke() ?? [];

        var discovered = new List<DiscoveredModelProcess>();

        foreach (var proc in allWmi.Values)
        {
            if (!targetSet.Contains(proc.Name))
                continue;

            var ports = portsByPid.TryGetValue(proc.Pid, out var lp) ? lp : [];

            // When isolated port range is configured (Phase 4A isolated test mode), ignore unrelated production processes
            if (_options.PortRangeMin.HasValue && _options.PortRangeMax.HasValue)
            {
                var inScope = ports.Any(p => p >= _options.PortRangeMin.Value && p <= _options.PortRangeMax.Value);
                if (!inScope)
                    continue;
            }

            var chain = BuildAncestorChain(proc.ParentPid, allWmi, secrets);
            var sanitizedCmd = LogRedactor.Redact(proc.CommandLine, secrets);
            var gguf = LogRedactor.TryExtractGgufModelPath(proc.CommandLine);
            var accessRestricted = string.IsNullOrWhiteSpace(proc.ExecutablePath) && string.IsNullOrWhiteSpace(proc.CommandLine);

            var (ownership, reason) = ClassifyOwnership(proc, chain, gatewayPid, accessRestricted);

            gpuByPid.TryGetValue(proc.Pid, out var perProcVram);

            discovered.Add(new DiscoveredModelProcess
            {
                ProcessId = proc.Pid,
                ParentProcessId = proc.ParentPid,
                ProcessName = proc.Name,
                ExecutablePath = proc.ExecutablePath,
                SanitizedCommandLine = sanitizedCmd,
                GgufModelPath = gguf,
                ListeningPorts = ports,
                AncestorChain = chain,
                GpuVramUsedMiB = perProcVram ?? (telemetry?.GpuUsedVramMiB),
                GpuUtilizationPct = telemetry?.GpuUtilizationPct,
                StartTimeUtc = proc.StartTimeUtc,
                AccessRestricted = accessRestricted,
                OwnershipClass = ownership,
                OwnershipReason = reason
            });
        }

        return discovered.OrderBy(d => d.ProcessId).ToArray();
    }

    public static (ProcessOwnershipClass Class, string Reason) ClassifyOwnership(
        WmiProcessRow proc,
        IReadOnlyList<ProcessTreeNode> ancestors,
        int? managedGatewayPid,
        bool accessRestricted)
    {
        if (accessRestricted)
        {
            return (ProcessOwnershipClass.UNKNOWN_OWNER,
                $"PID {proc.Pid} executable path & command line are inaccessible (AccessRestricted). Failing closed as UNKNOWN_OWNER.");
        }

        if (managedGatewayPid.HasValue && managedGatewayPid.Value > 0)
        {
            if (proc.ParentPid == managedGatewayPid.Value || ancestors.Any(a => a.Pid == managedGatewayPid.Value))
            {
                return (ProcessOwnershipClass.MANAGED_BY_LLAMA_SWAP,
                    $"Descendant of Guard-managed llama-swap Gateway PID {managedGatewayPid.Value}.");
            }
        }

        var cmd = proc.CommandLine ?? string.Empty;
        var hasCodexSignal =
            cmd.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || cmd.Contains("Round151", StringComparison.OrdinalIgnoreCase)
            || ancestors.Any(a =>
                a.Name.Contains("codex", StringComparison.OrdinalIgnoreCase)
                || (a.SanitizedCommandLine?.Contains("codex", StringComparison.OrdinalIgnoreCase) ?? false)
                || (a.ExecutablePath?.Contains("codex", StringComparison.OrdinalIgnoreCase) ?? false));

        if (hasCodexSignal)
        {
            return (ProcessOwnershipClass.EXTERNAL_CODEX_OWNED,
                $"Detected external Codex ownership signal in process tree / command line (PID={proc.Pid}, ParentPid={proc.ParentPid}).");
        }

        if (!string.IsNullOrWhiteSpace(proc.ExecutablePath))
        {
            return (ProcessOwnershipClass.EXTERNAL_UNMANAGED,
                $"External unmanaged model server outside Guard's llama-swap tree (PID={proc.Pid}, ParentPid={proc.ParentPid}, Image={proc.Name}).");
        }

        return (ProcessOwnershipClass.UNKNOWN_OWNER,
            $"Could not prove process ownership for PID {proc.Pid}. Failing closed as UNKNOWN_OWNER.");
    }

    private static IReadOnlyList<ProcessTreeNode> BuildAncestorChain(
        int? parentPid,
        Dictionary<int, WmiProcessRow> table,
        string?[] secrets)
    {
        var list = new List<ProcessTreeNode>();
        var visited = new HashSet<int>();
        var current = parentPid;
        var depth = 0;

        while (current.HasValue && current.Value > 0 && depth < 12 && visited.Add(current.Value))
        {
            if (!table.TryGetValue(current.Value, out var row))
                break;

            list.Add(new ProcessTreeNode(
                row.Pid,
                row.Name,
                row.ExecutablePath,
                LogRedactor.Redact(row.CommandLine, secrets)));
            current = row.ParentPid;
            depth++;
        }

        return list;
    }

    private static Dictionary<int, WmiProcessRow> QueryAllProcessesWmi()
    {
        var map = new Dictionary<int, WmiProcessRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                if (mo["ProcessId"] is null) continue;
                var pid = Convert.ToInt32(mo["ProcessId"]);
                int? ppid = mo["ParentProcessId"] is not null ? Convert.ToInt32(mo["ParentProcessId"]) : null;
                var name = mo["Name"]?.ToString() ?? string.Empty;
                var exe = mo["ExecutablePath"]?.ToString();
                var cmd = mo["CommandLine"]?.ToString();
                DateTimeOffset? created = null;
                if (mo["CreationDate"] is string dmtf && !string.IsNullOrWhiteSpace(dmtf))
                {
                    try { created = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(dmtf)).ToUniversalTime(); }
                    catch { /* ignore invalid DMTF */ }
                }
                map[pid] = new WmiProcessRow(pid, ppid, name, exe, cmd, created);
            }
        }
        catch
        {
            // Return whatever was collected; callers fail closed when discovery/telemetry is incomplete
        }
        return map;
    }

    public sealed record WmiProcessRow(
        int Pid,
        int? ParentPid,
        string Name,
        string? ExecutablePath,
        string? CommandLine,
        DateTimeOffset? StartTimeUtc);
}
