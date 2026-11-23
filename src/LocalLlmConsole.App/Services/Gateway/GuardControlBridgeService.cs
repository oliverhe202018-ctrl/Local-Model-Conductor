using LocalLLMManager.Guard.Contracts;

namespace LocalLlmConsole.Services;

public enum UnifiedDisplayState
{
    RUNNING,
    IDLE,
    LOADING,
    DRAINING,
    ROLLING_BACK,
    ROLLBACK_FAILED,
    FAULT_LOCKED,
    READ_ONLY_OBSERVATION,
    GUARD_OFFLINE,
    RECOVERY_REQUIRED,

    // Phase 4B backwards-compatibility aliases
    PROFILE_ACTIVE,
    MODEL_READY,
    MODEL_INFERENCE_VERIFIED,
    MODEL_LOADING,
    MODEL_DRAINING,
    MODEL_ERROR,
    MODEL_UNLOADED
}

public sealed record GuardSwitchStageProgress(
    int ActiveStageIndex,
    string StageName,
    bool Failed,
    string Detail);

public sealed record GuardOverviewSnapshot(
    bool GuardOnline,
    UnifiedDisplayState DisplayState,
    string FixedGatewayBaseUrl,
    string FixedLogicalModelId,
    string? ActiveProfile,
    string? LastKnownGoodProfile,
    string? ActiveModelId,
    string? FaultReason,
    bool InferencePaused,
    bool ExternalGpuConflictActive,
    string? ExternalConflictSummary,
    int InFlightRequestCount,
    int QueuedRequestCount,
    double LoadingProgressPct,
    DateTimeOffset? LastSwitchTimeUtc,
    string RollbackStatusText,
    GuardSwitchStageProgress SwitchStage,
    GuardStatusSnapshot? RawGuardStatus,
    IReadOnlyList<GuardAuditEntry> RecentAudit);

/// <summary>
/// Phase 4B-2 Official Guard Client Adapter Layer for Windows Manager.
/// Communicates exclusively through Guard's Windows ACL Named Pipe (GuardPipeClient).
/// NEVER holds Guard's internal llama-swap admin key.
/// Automatically enters READ_ONLY_OBSERVATION whenever Guard is offline or an external GPU owner is active.
/// </summary>
public sealed class GuardControlBridgeService
{
    public static readonly string[] LifecycleStages =
    [
        "资源检查",
        "外部进程检查",
        "请求排空",
        "旧模型卸载",
        "新模型启动",
        "健康检查",
        "推理验收",
        "切换完成"
    ];

    private readonly GuardBackendOptions _options;
    private readonly GuardPipeClient _pipeClient;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeSwitchCts;
    private DateTimeOffset? _lastSwitchTimeUtc;
    private string _lastRollbackText = "就绪（未触发回滚）";
    private string? _lastErrorMessage;
    private bool _lastSwitchVerified;
    private GuardSwitchStageProgress _currentStage = new(0, "待命", false, "等待操作指令");

    public GuardControlBridgeService(GuardBackendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pipeClient = new GuardPipeClient(options.GuardPipeName);
    }

    public GuardBackendOptions Options => _options;

    public async Task<GuardOverviewSnapshot> PollStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var statusResp = await _pipeClient.SendAsync(new GuardCommandRequest
            {
                Command = GuardCommandType.GET_STATUS,
                CallerRole = GuardCallerRole.UiClient,
                CallerId = $"WM-UI:{Environment.UserName}"
            }, connectTimeoutMs: 2000, cancellationToken: ct).ConfigureAwait(false);

            var auditResp = await _pipeClient.SendAsync(new GuardCommandRequest
            {
                Command = GuardCommandType.GET_AUDIT_TAIL,
                CallerRole = GuardCallerRole.UiClient,
                CallerId = $"WM-UI:{Environment.UserName}"
            }, connectTimeoutMs: 2000, cancellationToken: ct).ConfigureAwait(false);

            var raw = statusResp.Status;
            var displayState = ResolveDisplayState(raw, _lastSwitchVerified, _lastErrorMessage);
            var activeModel = raw?.RunningManagedModels.FirstOrDefault();

            return new GuardOverviewSnapshot(
                GuardOnline: true,
                DisplayState: displayState,
                FixedGatewayBaseUrl: _options.FixedGatewayBaseUrl,
                FixedLogicalModelId: _options.FixedLogicalModelId,
                ActiveProfile: raw?.ActiveProfile ?? statusResp.ActiveProfile,
                LastKnownGoodProfile: raw?.LastKnownGoodProfile ?? statusResp.LastKnownGoodProfile,
                ActiveModelId: activeModel,
                FaultReason: raw?.FaultLockReason ?? _lastErrorMessage,
                InferencePaused: raw?.InferencePaused ?? false,
                ExternalGpuConflictActive: raw?.ExternalGpuConflictActive ?? false,
                ExternalConflictSummary: raw?.ExternalConflictSummary,
                InFlightRequestCount: raw?.InFlightRequestCount ?? 0,
                QueuedRequestCount: raw?.QueuedRequestCount ?? 0,
                LoadingProgressPct: displayState is UnifiedDisplayState.MODEL_READY or UnifiedDisplayState.MODEL_INFERENCE_VERIFIED ? 100
                    : displayState == UnifiedDisplayState.MODEL_LOADING ? 55
                    : displayState == UnifiedDisplayState.MODEL_DRAINING ? 25 : 0,
                LastSwitchTimeUtc: _lastSwitchTimeUtc,
                RollbackStatusText: _lastRollbackText,
                SwitchStage: _currentStage,
                RawGuardStatus: raw,
                RecentAudit: auditResp.AuditEntries ?? []);
        }
        catch (Exception ex)
        {
            return new GuardOverviewSnapshot(
                GuardOnline: false,
                DisplayState: UnifiedDisplayState.GUARD_OFFLINE,
                FixedGatewayBaseUrl: _options.FixedGatewayBaseUrl,
                FixedLogicalModelId: _options.FixedLogicalModelId,
                ActiveProfile: null,
                LastKnownGoodProfile: null,
                ActiveModelId: null,
                FaultReason: $"Guard 控制服务离线 ({ex.Message}) — 已自动进入只读观察模式。",
                InferencePaused: false,
                ExternalGpuConflictActive: false,
                ExternalConflictSummary: "Guard 离线：禁止一切写操作，仅保留资产查看与日志检索。",
                InFlightRequestCount: 0,
                QueuedRequestCount: 0,
                LoadingProgressPct: 0,
                LastSwitchTimeUtc: _lastSwitchTimeUtc,
                RollbackStatusText: "Guard 离线",
                SwitchStage: new GuardSwitchStageProgress(0, "只读观察", true, "无法连接 Guard ACL Named Pipe"),
                RawGuardStatus: null,
                RecentAudit: []);
        }
    }

    public async Task<GuardCommandResponse> SwitchProfileAsync(
        string targetProfile,
        GuardCallerRole callerRole = GuardCallerRole.UiClient,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        CancellationTokenSource linked;
        lock (_sync)
        {
            _activeSwitchCts?.Cancel();
            _activeSwitchCts?.Dispose();
            _activeSwitchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked = _activeSwitchCts;
        }

        _currentStage = new GuardSwitchStageProgress(0, "资源检查 → 外部进程检查", false, $"向 Guard 提交切换至 '{targetProfile}'...");
        _lastErrorMessage = null;

        try
        {
            var resp = await _pipeClient.SendAsync(new GuardCommandRequest
            {
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
                Command = GuardCommandType.SWITCH_PROFILE,
                CallerRole = callerRole,
                CallerId = $"{callerRole}:{Environment.UserName}",
                TargetProfile = targetProfile,
                TimeoutSeconds = 60
            }, connectTimeoutMs: 4000, cancellationToken: linked.Token).ConfigureAwait(false);

            _lastSwitchTimeUtc = DateTimeOffset.UtcNow;
            if (resp.Succeeded)
            {
                _lastSwitchVerified = true;
                _lastRollbackText = $"切换成功 — 当前健康基线: {resp.LastKnownGoodProfile}";
                _currentStage = new GuardSwitchStageProgress(7, "切换完成", false, resp.Message);
            }
            else if (resp.RollbackOutcome == RollbackOutcome.ROLLBACK_SUCCEEDED)
            {
                _lastSwitchVerified = true;
                _lastErrorMessage = resp.Message;
                _lastRollbackText = $"已自动回滚至 {resp.ActiveProfile} (失败原因: {resp.FailureClass})";
                _currentStage = new GuardSwitchStageProgress(5, "回滚成功", true, resp.Message);
            }
            else if (resp.OperationalState == GuardOperationalState.FAULT_LOCKED)
            {
                _lastSwitchVerified = false;
                _lastErrorMessage = resp.Message;
                _lastRollbackText = "双重故障锁定 (ROLLBACK_FAILED / FAULT_LOCKED)";
                _currentStage = new GuardSwitchStageProgress(6, "故障锁定", true, resp.Message);
            }
            else
            {
                _lastErrorMessage = resp.Message;
                _currentStage = new GuardSwitchStageProgress(1, "门禁拦截", true, resp.Message);
            }

            return resp;
        }
        catch (OperationCanceledException)
        {
            _currentStage = new GuardSwitchStageProgress(2, "已取消等待", true, "操作方已取消当前等待");
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                Code = "CLIENT_WAIT_CANCELLED",
                Message = "已取消客户端等待（正在运行的模型请求不会被强杀）。",
                OperationalState = GuardOperationalState.READ_ONLY_OBSERVATION
            };
        }
        catch (Exception ex)
        {
            _lastErrorMessage = ex.Message;
            _currentStage = new GuardSwitchStageProgress(0, "Guard 离线", true, ex.Message);
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                Code = "GUARD_OFFLINE",
                Message = $"无法连接 Guard 控制服务，已锁定只读模式: {ex.Message}",
                OperationalState = GuardOperationalState.READ_ONLY_OBSERVATION
            };
        }
    }

    public void CancelPendingWait()
    {
        lock (_sync)
            _activeSwitchCts?.Cancel();
    }

    public Task<GuardCommandResponse> PauseAsync(GuardCallerRole role = GuardCallerRole.UiClient, CancellationToken ct = default)
        => SendSimpleCommandAsync(GuardCommandType.PAUSE_INFERENCE, role, null, "User requested pause", ct);

    public Task<GuardCommandResponse> ResumeAsync(GuardCallerRole role = GuardCallerRole.UiClient, CancellationToken ct = default)
        => SendSimpleCommandAsync(GuardCommandType.RESUME_INFERENCE, role, null, "User requested resume", ct);

    public Task<GuardCommandResponse> UnloadAsync(GuardCallerRole role = GuardCallerRole.UiClient, CancellationToken ct = default)
        => SendSimpleCommandAsync(GuardCommandType.UNLOAD_ALL, role, null, "User requested safe unload", ct);

    public Task<GuardCommandResponse> UnlockFaultAsync(string reason, GuardCallerRole role = GuardCallerRole.UiClient, CancellationToken ct = default)
        => SendSimpleCommandAsync(GuardCommandType.UNLOCK_FAULT, role, null, reason, ct);

    public async Task<GuardCommandResponse> RollbackToBaselineAsync(GuardCallerRole role = GuardCallerRole.UiClient, CancellationToken ct = default)
    {
        var snap = await PollStatusAsync(ct).ConfigureAwait(false);
        if (!snap.GuardOnline || string.IsNullOrWhiteSpace(snap.LastKnownGoodProfile))
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                Code = "ROLLBACK_BASELINE_UNAVAILABLE",
                Message = "Guard 离线或不存在可回滚的健康基线 Profile。",
                OperationalState = UnifiedToGuardState(snap.DisplayState)
            };
        }
        return await SwitchProfileAsync(snap.LastKnownGoodProfile, role, ct: ct).ConfigureAwait(false);
    }

    private async Task<GuardCommandResponse> SendSimpleCommandAsync(
        GuardCommandType command,
        GuardCallerRole role,
        string? targetProfile,
        string? reason,
        CancellationToken ct)
    {
        try
        {
            return await _pipeClient.SendAsync(new GuardCommandRequest
            {
                Command = command,
                CallerRole = role,
                CallerId = $"{role}:{Environment.UserName}",
                TargetProfile = targetProfile,
                Reason = reason
            }, connectTimeoutMs: 3000, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                Code = "GUARD_OFFLINE",
                Message = $"Guard 离线: {ex.Message}",
                OperationalState = GuardOperationalState.READ_ONLY_OBSERVATION
            };
        }
    }

    public static UnifiedDisplayState ResolveDisplayState(
        GuardStatusSnapshot? raw,
        bool inferenceVerified,
        string? lastError)
    {
        if (raw is null)
            return UnifiedDisplayState.GUARD_OFFLINE;

        if (raw.OperationalState == GuardOperationalState.FAULT_LOCKED)
            return string.IsNullOrWhiteSpace(lastError) ? UnifiedDisplayState.FAULT_LOCKED : UnifiedDisplayState.ROLLBACK_FAILED;
        if (raw.OperationalState == GuardOperationalState.RECOVERY_REQUIRED)
            return UnifiedDisplayState.RECOVERY_REQUIRED;
        if (raw.OperationalState == GuardOperationalState.ROLLING_BACK)
            return UnifiedDisplayState.ROLLING_BACK;
        if (raw.OperationalState == GuardOperationalState.DRAINING)
            return UnifiedDisplayState.DRAINING;
        if (raw.OperationalState == GuardOperationalState.LOADING)
            return UnifiedDisplayState.LOADING;

        if (!raw.GatewayHealthy)
            return UnifiedDisplayState.READ_ONLY_OBSERVATION;
        if (raw.ExternalGpuConflictActive || raw.OperationalState == GuardOperationalState.READ_ONLY_OBSERVATION)
            return UnifiedDisplayState.READ_ONLY_OBSERVATION;

        var hasRunning = raw.RunningManagedModels.Count > 0;
        if (hasRunning)
            return inferenceVerified ? UnifiedDisplayState.MODEL_INFERENCE_VERIFIED : UnifiedDisplayState.MODEL_READY;

        // CRITICAL RULE (§6A): Never report PROFILE_ACTIVE as loaded when RunningManagedModels is empty!
        if (!string.IsNullOrWhiteSpace(raw.ActiveProfile))
            return string.IsNullOrWhiteSpace(lastError) ? UnifiedDisplayState.PROFILE_ACTIVE : UnifiedDisplayState.ROLLBACK_FAILED;

        return UnifiedDisplayState.IDLE;
    }

    private static GuardOperationalState UnifiedToGuardState(UnifiedDisplayState s) => s switch
    {
        UnifiedDisplayState.FAULT_LOCKED => GuardOperationalState.FAULT_LOCKED,
        UnifiedDisplayState.RECOVERY_REQUIRED => GuardOperationalState.RECOVERY_REQUIRED,
        UnifiedDisplayState.ROLLING_BACK => GuardOperationalState.ROLLING_BACK,
        UnifiedDisplayState.DRAINING or UnifiedDisplayState.MODEL_DRAINING => GuardOperationalState.DRAINING,
        UnifiedDisplayState.LOADING or UnifiedDisplayState.MODEL_LOADING => GuardOperationalState.LOADING,
        UnifiedDisplayState.RUNNING or UnifiedDisplayState.MODEL_READY or UnifiedDisplayState.MODEL_INFERENCE_VERIFIED => GuardOperationalState.RUNNING_HEALTHY,
        _ => GuardOperationalState.READ_ONLY_OBSERVATION
    };
}
