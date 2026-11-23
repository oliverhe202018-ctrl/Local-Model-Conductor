using System.Text.Json.Serialization;

namespace LocalLLMManager.Guard.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ProcessOwnershipClass>))]
public enum ProcessOwnershipClass
{
    MANAGED_BY_LLAMA_SWAP,
    EXTERNAL_CODEX_OWNED,
    EXTERNAL_UNMANAGED,
    UNKNOWN_OWNER
}

[JsonConverter(typeof(JsonStringEnumConverter<GuardOperationalState>))]
public enum GuardOperationalState
{
    READ_ONLY_OBSERVATION,
    ALLOW_SWITCH,
    DRAINING,
    LOADING,
    RUNNING_HEALTHY,
    ROLLING_BACK,
    FAULT_LOCKED,
    RECOVERY_REQUIRED
}

[JsonConverter(typeof(JsonStringEnumConverter<SwitchFailureClass>))]
public enum SwitchFailureClass
{
    NONE,
    DENIED_BY_GATE,
    MODEL_STARTUP_FAILED,
    MODEL_HEALTH_CHECK_FAILED,
    MODEL_INFERENCE_FAILED,
    DRAIN_TIMEOUT,
    GATEWAY_UNREACHABLE
}

[JsonConverter(typeof(JsonStringEnumConverter<RollbackOutcome>))]
public enum RollbackOutcome
{
    NOT_TRIGGERED,
    ROLLBACK_SUCCEEDED,
    ROLLBACK_FAILED
}

[JsonConverter(typeof(JsonStringEnumConverter<GuardCommandType>))]
public enum GuardCommandType
{
    GET_STATUS,
    SWITCH_PROFILE,
    PAUSE_INFERENCE,
    RESUME_INFERENCE,
    UNLOAD_ALL,
    UNLOCK_FAULT,
    GET_AUDIT_TAIL
}

[JsonConverter(typeof(JsonStringEnumConverter<GuardCallerRole>))]
public enum GuardCallerRole
{
    UiClient,
    CliClient,
    InternalGuard
}

public sealed record ProcessTreeNode(
    int Pid,
    string Name,
    string? ExecutablePath,
    string? SanitizedCommandLine);

public sealed record DiscoveredModelProcess
{
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string SanitizedCommandLine { get; init; } = string.Empty;
    public string? GgufModelPath { get; init; }
    public IReadOnlyList<int> ListeningPorts { get; init; } = [];
    public IReadOnlyList<ProcessTreeNode> AncestorChain { get; init; } = [];
    public double? GpuVramUsedMiB { get; init; }
    public double? GpuUtilizationPct { get; init; }
    public DateTimeOffset? StartTimeUtc { get; init; }
    public bool AccessRestricted { get; init; }
    public ProcessOwnershipClass OwnershipClass { get; init; } = ProcessOwnershipClass.UNKNOWN_OWNER;
    public string OwnershipReason { get; init; } = string.Empty;
}

public sealed record GpuComputeProcessEntry(
    int ProcessId,
    string ProcessName,
    double? UsedVramMiB);

public sealed record HostHardwareTelemetry
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool GpuAvailable { get; init; }
    public string? GpuName { get; init; }
    public double? GpuTotalVramMiB { get; init; }
    public double? GpuUsedVramMiB { get; init; }
    public double? GpuFreeVramMiB { get; init; }
    public double? GpuUtilizationPct { get; init; }
    public double? GpuTemperatureC { get; init; }
    public double? GpuPowerDrawW { get; init; }
    public IReadOnlyList<GpuComputeProcessEntry> GpuProcesses { get; init; } = [];

    public bool RamAvailable { get; init; }
    public double? RamTotalMiB { get; init; }
    public double? RamAvailableMiB { get; init; }

    public bool CommitAvailable { get; init; }
    public double? CommitLimitMiB { get; init; }
    public double? CommitUsedMiB { get; init; }
    public double? CommitAvailableMiB { get; init; }

    public string? DiagnosticError { get; init; }
}

public sealed record GuardProfileSpec
{
    public string ProfileName { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public int UpstreamPort { get; init; }
    public string RuntimeExecutablePath { get; init; } = string.Empty;
    public string? ExpectedRuntimeSha256 { get; init; }
    public string? GgufPath { get; init; }
    public int ContextLength { get; init; }
    public double RequiredVramMiB { get; init; }
    public double SafetyVramMarginMiB { get; init; } = 1024;
    public double RequiredRamMiB { get; init; }
    public double SafetyRamMarginMiB { get; init; } = 1024;
    public double RequiredCommitMiB { get; init; }
    public double SafetyCommitMarginMiB { get; init; } = 1024;
    public double UnknownGpuUsageThresholdMiB { get; init; } = 2500;
    public bool IsIsolatedMockProfile { get; init; }
}

public sealed record ResourceGateEvaluation
{
    public bool Allowed { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? BlockingOwner { get; init; }
    public double? GpuFreeMiB { get; init; }
    public double? GpuUsedMiB { get; init; }
    public double? GpuTotalMiB { get; init; }
    public double? AvailableRamMiB { get; init; }
    public double? AvailableCommitMiB { get; init; }
    public string? ComputedRuntimeSha256 { get; init; }
    public bool? RuntimeSha256Matched { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GuardCommandRequest
{
    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");
    public GuardCommandType Command { get; init; }
    public GuardCallerRole CallerRole { get; init; } = GuardCallerRole.CliClient;
    public string CallerId { get; init; } = Environment.UserName;
    public string? TargetProfile { get; init; }
    public string? Reason { get; init; }
    public int TimeoutSeconds { get; init; } = 45;
    public bool ForceGpuGateEvaluationOnMock { get; init; }
}

public sealed record GuardCommandResponse
{
    public bool Accepted { get; init; }
    public bool Succeeded { get; init; }
    public bool DeduplicatedReplay { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public GuardOperationalState OperationalState { get; init; }
    public SwitchFailureClass FailureClass { get; init; } = SwitchFailureClass.NONE;
    public RollbackOutcome RollbackOutcome { get; init; } = RollbackOutcome.NOT_TRIGGERED;
    public string? ActiveProfile { get; init; }
    public string? LastKnownGoodProfile { get; init; }
    public ResourceGateEvaluation? GateEvaluation { get; init; }
    public GuardStatusSnapshot? Status { get; init; }
    public IReadOnlyList<GuardAuditEntry>? AuditEntries { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GuardStatusSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int GuardPid { get; init; }
    public int? LlamaSwapGatewayPid { get; init; }
    public bool GatewayHealthy { get; init; }
    public bool InferencePaused { get; init; }
    public GuardOperationalState OperationalState { get; init; }
    public string? ActiveProfile { get; init; }
    public string? LastKnownGoodProfile { get; init; }
    public string? FaultLockReason { get; init; }
    public int InFlightRequestCount { get; init; }
    public int QueuedRequestCount { get; init; }
    public IReadOnlyList<string> RunningManagedModels { get; init; } = [];
    public IReadOnlyList<DiscoveredModelProcess> DiscoveredProcesses { get; init; } = [];
    public HostHardwareTelemetry? Telemetry { get; init; }
    public bool ExternalGpuConflictActive { get; init; }
    public string? ExternalConflictSummary { get; init; }
}

public sealed record GuardPersistedState
{
    public string? ActiveProfile { get; init; }
    public string? LastKnownGoodProfile { get; init; }
    public GuardOperationalState OperationalState { get; init; } = GuardOperationalState.ALLOW_SWITCH;
    public string? FaultLockReason { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, GuardCommandResponse> RecentIdempotentResponses { get; init; } = new(StringComparer.Ordinal);
}

public sealed record GuardAuditEntry
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string CallerRole { get; init; } = string.Empty;
    public string CallerId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ResultCode { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public GuardOperationalState StateAfter { get; init; }
}
