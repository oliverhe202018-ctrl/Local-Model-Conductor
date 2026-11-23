using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Discovery;
using LocalLLMManager.Guard.Service.Gate;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Control;

public sealed class GuardCoordinatorOptions
{
    public string StableLogicalModelId { get; init; } = "local-coder";
    public IReadOnlyDictionary<string, GuardProfileSpec> Profiles { get; init; } = new Dictionary<string, GuardProfileSpec>(StringComparer.OrdinalIgnoreCase);
    public Func<int?>? ManagedGatewayPidProvider { get; init; }
    public Func<string?[]>? KnownSecretsProvider { get; init; }
    public int DrainTimeoutSeconds { get; init; } = 15;
    public int PortReleaseWaitMs { get; init; } = 3000;
}

/// <summary>
/// Single serialized control plane entry point for LocalLLMManager.Guard.
///
/// Guarantees:
/// - Strict SemaphoreSlim(1,1) serialization across all UI/CLI commands
/// - Idempotency key deduplication & duplicate-command protection
/// - Live read-only external discovery (UNKNOWN_OWNER & external GPU owners fail closed!)
/// - Full 8-rule Resource Safety Gate (VRAM, RAM, Commit, Telemetry, External, Old Exit, Port Free, Runtime SHA-256)
/// - Zero-restart profile switching via llama-swap PUT /api/profiles/active
/// - 7-step automatic rollback + explicit FAULT_LOCKED on double fault
/// - NEVER spawns or kills llama-server.exe directly!
/// </summary>
public sealed partial class GuardControlCoordinator : IDisposable
{
    private readonly GuardCoordinatorOptions _options;
    private readonly LlamaSwapAdminClient _llamaSwap;
    private readonly IExternalProcessDiscoveryService _discovery;
    private readonly IHostTelemetryCollector _telemetry;
    private readonly GuardStateStore _stateStore;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private GuardPersistedState _state;
    private GuardStatusSnapshot? _lastStatus;

    /// <summary>Optional test hook to inject deterministic rollback failure / port occupancy without touching host OS.</summary>
    public Func<int, int?>? PortOccupiedOverride { get; set; }
    public Func<string, Task>? BeforeRollbackRestoreHookAsync { get; set; }

    public GuardControlCoordinator(
        GuardCoordinatorOptions options,
        LlamaSwapAdminClient llamaSwap,
        IExternalProcessDiscoveryService discovery,
        IHostTelemetryCollector telemetry,
        GuardStateStore stateStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _llamaSwap = llamaSwap ?? throw new ArgumentNullException(nameof(llamaSwap));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

        _state = _stateStore.LoadState();
        if (_state.OperationalState == GuardOperationalState.DRAINING
            || _state.OperationalState == GuardOperationalState.LOADING
            || _state.OperationalState == GuardOperationalState.ROLLING_BACK)
        {
            var priorState = _state.OperationalState;
            _state = _state with
            {
                OperationalState = GuardOperationalState.RECOVERY_REQUIRED,
                FaultLockReason = $"Guard crashed or restarted during uncompleted transaction (prior state was {priorState}). Operator recovery required."
            };
            _stateStore.SaveState(_state);
        }
        _stateStore.AppendAudit(new GuardAuditEntry
        {
            EventType = "GUARD_INITIALIZED",
            CallerRole = GuardCallerRole.InternalGuard.ToString(),
            CallerId = $"PID:{Environment.ProcessId}",
            Action = "STARTUP_RECOVER_STATE",
            ResultCode = "RECOVERED",
            Summary = $"Guard initialized. Recovered state={_state.OperationalState}, activeProfile={_state.ActiveProfile ?? "none"}, lastKnownGood={_state.LastKnownGoodProfile ?? "none"}.",
            StateAfter = _state.OperationalState
        });
    }

    public async Task<GuardStatusSnapshot> RefreshObservationAsync(CancellationToken ct = default)
    {
        var telemetry = await _telemetry.CollectAsync(ct).ConfigureAwait(false);
        var discovered = await _discovery.DiscoverAsync(telemetry, ct).ConfigureAwait(false);
        var gwHealthy = await _llamaSwap.IsHealthyAsync(ct).ConfigureAwait(false);

        string? activeProfile = _state.ActiveProfile;
        IReadOnlyList<string> running = [];
        int inflight = 0;
        bool paused = false;

        if (gwHealthy)
        {
            var (liveProfile, _) = await _llamaSwap.GetProfilesAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(liveProfile))
                activeProfile = liveProfile;
            running = await _llamaSwap.GetRunningModelsAsync(ct).ConfigureAwait(false);
            var ev = await _llamaSwap.ReadEventSnapshotAsync(ct).ConfigureAwait(false);
            if (ev.SnapshotReceived)
            {
                inflight = ev.InFlightCount;
                paused = ev.IsPaused;
            }
        }

        var externalOrUnknown = discovered.Where(p => p.OwnershipClass != ProcessOwnershipClass.MANAGED_BY_LLAMA_SWAP).ToList();
        var hasExternalConflict = externalOrUnknown.Count > 0;
        var conflictSummary = hasExternalConflict
            ? string.Join("; ", externalOrUnknown.Select(p => $"{p.OwnershipClass} PID={p.ProcessId} Ports=[{string.Join(",", p.ListeningPorts)}]"))
            : null;

        var effectiveState = _state.OperationalState;
        if (effectiveState != GuardOperationalState.FAULT_LOCKED
            && effectiveState != GuardOperationalState.RECOVERY_REQUIRED
            && effectiveState != GuardOperationalState.DRAINING
            && effectiveState != GuardOperationalState.LOADING
            && effectiveState != GuardOperationalState.ROLLING_BACK)
        {
            effectiveState = hasExternalConflict
                ? GuardOperationalState.READ_ONLY_OBSERVATION
                : (running.Count > 0 ? GuardOperationalState.RUNNING_HEALTHY : GuardOperationalState.ALLOW_SWITCH);
        }

        var snapshot = new GuardStatusSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            GuardPid = Environment.ProcessId,
            LlamaSwapGatewayPid = _options.ManagedGatewayPidProvider?.Invoke(),
            GatewayHealthy = gwHealthy,
            InferencePaused = paused,
            OperationalState = effectiveState,
            ActiveProfile = activeProfile,
            LastKnownGoodProfile = _state.LastKnownGoodProfile ?? activeProfile,
            FaultLockReason = _state.FaultLockReason,
            InFlightRequestCount = inflight,
            QueuedRequestCount = 0,
            RunningManagedModels = running,
            DiscoveredProcesses = discovered,
            Telemetry = telemetry,
            ExternalGpuConflictActive = hasExternalConflict,
            ExternalConflictSummary = conflictSummary
        };

        _lastStatus = snapshot;
        return snapshot;
    }

    public async Task<GuardCommandResponse> ExecuteCommandAsync(GuardCommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Command == GuardCommandType.GET_STATUS)
        {
            var status = await RefreshObservationAsync(ct).ConfigureAwait(false);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = true,
                IdempotencyKey = request.IdempotencyKey,
                Code = "OK",
                Message = "Live Guard status snapshot collected (read-only).",
                OperationalState = status.OperationalState,
                ActiveProfile = status.ActiveProfile,
                LastKnownGoodProfile = status.LastKnownGoodProfile,
                Status = status
            };
        }

        if (request.Command == GuardCommandType.GET_AUDIT_TAIL)
        {
            var entries = _stateStore.ReadRecentAudit(50);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = true,
                IdempotencyKey = request.IdempotencyKey,
                Code = "OK",
                Message = $"Returned {entries.Count} redacted audit entries.",
                OperationalState = _state.OperationalState,
                ActiveProfile = _state.ActiveProfile,
                LastKnownGoodProfile = _state.LastKnownGoodProfile,
                AuditEntries = entries
            };
        }

        // Serialize ALL mutating commands with timeout
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 180)));

        bool lockAcquired = false;
        try
        {
            lockAcquired = await _operationLock.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 180)), opCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lockAcquired = false;
        }

        if (!lockAcquired)
        {
            return RecordAndReturn(request, new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "OPERATION_MUTEX_TIMEOUT",
                Message = "Timed out waiting for Guard operation mutex.",
                OperationalState = _state.OperationalState,
                ActiveProfile = _state.ActiveProfile,
                LastKnownGoodProfile = _state.LastKnownGoodProfile
            });
        }

        try
        {
            // Idempotency deduplication check
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
                _state.RecentIdempotentResponses.TryGetValue(request.IdempotencyKey, out var previous))
            {
                var replayed = previous with { DeduplicatedReplay = true };
                _stateStore.AppendAudit(new GuardAuditEntry
                {
                    EventType = "COMMAND_DEDUP_REPLAY",
                    IdempotencyKey = request.IdempotencyKey,
                    CallerRole = request.CallerRole.ToString(),
                    CallerId = request.CallerId,
                    Action = request.Command.ToString(),
                    ResultCode = replayed.Code,
                    Summary = $"Replayed cached idempotent response for key '{request.IdempotencyKey}'.",
                    StateAfter = _state.OperationalState
                });
                return replayed;
            }

            var response = request.Command switch
            {
                GuardCommandType.SWITCH_PROFILE => await ExecuteSwitchProfileLockedAsync(request, opCts.Token).ConfigureAwait(false),
                GuardCommandType.PAUSE_INFERENCE => await ExecutePauseLockedAsync(request, opCts.Token).ConfigureAwait(false),
                GuardCommandType.RESUME_INFERENCE => await ExecuteResumeLockedAsync(request, opCts.Token).ConfigureAwait(false),
                GuardCommandType.UNLOAD_ALL => await ExecuteUnloadLockedAsync(request, opCts.Token).ConfigureAwait(false),
                GuardCommandType.UNLOCK_FAULT => await ExecuteUnlockFaultLockedAsync(request, opCts.Token).ConfigureAwait(false),
                _ => new GuardCommandResponse
                {
                    Accepted = false,
                    Succeeded = false,
                    IdempotencyKey = request.IdempotencyKey,
                    Code = "UNSUPPORTED_COMMAND",
                    Message = $"Unsupported Guard command: {request.Command}",
                    OperationalState = _state.OperationalState
                }
            };

            return RecordAndReturn(request, response);
        }
        catch (Exception ex)
        {
            var errResp = new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "COMMAND_EXECUTION_ERROR",
                Message = $"Command execution failed safely: {ex.Message}",
                OperationalState = _state.OperationalState
            };
            return RecordAndReturn(request, errResp);
        }
        finally
        {
            _operationLock.Release();
        }
    }


    private async Task<GuardCommandResponse> ExecutePauseLockedAsync(GuardCommandRequest request, CancellationToken ct)
    {
        var obs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        if (obs.InFlightRequestCount > 0)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "BLOCKED_INFLIGHT_REQUESTS_ACTIVE",
                Message = $"Refused to pause inference while {obs.InFlightRequestCount} in-flight request(s) are active (never force-kill active streams).",
                OperationalState = obs.OperationalState,
                Status = obs
            };
        }

        var (ok, status, body) = await _llamaSwap.PauseInferenceAsync(ct).ConfigureAwait(false);
        var after = await RefreshObservationAsync(ct).ConfigureAwait(false);
        return new GuardCommandResponse
        {
            Accepted = true,
            Succeeded = ok,
            IdempotencyKey = request.IdempotencyKey,
            Code = ok ? "INFERENCE_PAUSED" : "PAUSE_FAILED",
            Message = ok ? "llama-swap inference paused via Guard admin channel." : $"Pause failed (HTTP {status}): {body}",
            OperationalState = after.OperationalState,
            ActiveProfile = after.ActiveProfile,
            LastKnownGoodProfile = after.LastKnownGoodProfile,
            Status = after
        };
    }

    private async Task<GuardCommandResponse> ExecuteResumeLockedAsync(GuardCommandRequest request, CancellationToken ct)
    {
        var obs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        if (obs.ExternalGpuConflictActive)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "BLOCKED_BY_EXTERNAL_GPU_OWNER",
                Message = $"Cannot resume local model loading while external GPU owner is active: {obs.ExternalConflictSummary}",
                OperationalState = GuardOperationalState.READ_ONLY_OBSERVATION,
                Status = obs
            };
        }

        var (ok, status, body) = await _llamaSwap.ResumeInferenceAsync(ct).ConfigureAwait(false);
        var after = await RefreshObservationAsync(ct).ConfigureAwait(false);
        return new GuardCommandResponse
        {
            Accepted = true,
            Succeeded = ok,
            IdempotencyKey = request.IdempotencyKey,
            Code = ok ? "INFERENCE_RESUMED" : "RESUME_FAILED",
            Message = ok ? "llama-swap inference resumed via Guard admin channel." : $"Resume failed (HTTP {status}): {body}",
            OperationalState = after.OperationalState,
            ActiveProfile = after.ActiveProfile,
            LastKnownGoodProfile = after.LastKnownGoodProfile,
            Status = after
        };
    }

    private async Task<GuardCommandResponse> ExecuteUnloadLockedAsync(GuardCommandRequest request, CancellationToken ct)
    {
        var obs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        if (obs.ExternalGpuConflictActive)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "BLOCKED_BY_EXTERNAL_GPU_OWNER",
                Message = $"Unload command blocked in READ_ONLY_OBSERVATION while external model process is active: {obs.ExternalConflictSummary}",
                OperationalState = GuardOperationalState.READ_ONLY_OBSERVATION,
                Status = obs
            };
        }

        if (obs.InFlightRequestCount > 0)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "BLOCKED_INFLIGHT_REQUESTS_ACTIVE",
                Message = $"Refused to unload while {obs.InFlightRequestCount} active request(s) are streaming.",
                OperationalState = obs.OperationalState,
                Status = obs
            };
        }

        var (ok, status, body) = await _llamaSwap.UnloadAllAsync(ct).ConfigureAwait(false);
        if (ok && _state.OperationalState != GuardOperationalState.FAULT_LOCKED)
            UpdateState(GuardOperationalState.ALLOW_SWITCH, obs.ActiveProfile, _state.LastKnownGoodProfile, null);
        var after = await RefreshObservationAsync(ct).ConfigureAwait(false);
        return new GuardCommandResponse
        {
            Accepted = true,
            Succeeded = ok,
            IdempotencyKey = request.IdempotencyKey,
            Code = ok ? "MODELS_UNLOADED" : "UNLOAD_FAILED",
            Message = ok ? "All managed models unloaded via llama-swap admin API." : $"Unload failed (HTTP {status}): {body}",
            OperationalState = after.OperationalState,
            ActiveProfile = after.ActiveProfile,
            LastKnownGoodProfile = after.LastKnownGoodProfile,
            Status = after
        };
    }

    private async Task<GuardCommandResponse> ExecuteUnlockFaultLockedAsync(GuardCommandRequest request, CancellationToken ct)
    {
        UpdateState(GuardOperationalState.ALLOW_SWITCH, _state.ActiveProfile, _state.LastKnownGoodProfile, null);
        var after = await RefreshObservationAsync(ct).ConfigureAwait(false);
        return new GuardCommandResponse
        {
            Accepted = true,
            Succeeded = true,
            IdempotencyKey = request.IdempotencyKey,
            Code = "FAULT_UNLOCKED",
            Message = $"Operator '{request.CallerId}' cleared FAULT_LOCKED ({request.Reason ?? "manual unlock"}).",
            OperationalState = after.OperationalState,
            ActiveProfile = after.ActiveProfile,
            LastKnownGoodProfile = after.LastKnownGoodProfile,
            Status = after
        };
    }

    private int? GetOccupiedPortPid(int port)
    {
        if (PortOccupiedOverride is not null)
            return PortOccupiedOverride(port);

        var listeners = WindowsTcpPortTable.GetAllListeningTcpPorts();
        var match = listeners.FirstOrDefault(l => l.Port == port);
        return match?.ProcessId;
    }

    private void UpdateState(GuardOperationalState state, string? activeProfile, string? lastKnownGood, string? faultReason)
    {
        _state = _state with
        {
            OperationalState = state,
            ActiveProfile = activeProfile,
            LastKnownGoodProfile = lastKnownGood,
            FaultLockReason = faultReason,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _stateStore.SaveState(_state);
    }

    private GuardCommandResponse RecordAndReturn(GuardCommandRequest request, GuardCommandResponse response)
    {
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var map = new Dictionary<string, GuardCommandResponse>(_state.RecentIdempotentResponses, StringComparer.Ordinal)
            {
                [request.IdempotencyKey] = response
            };
            _state = _state with { RecentIdempotentResponses = map, UpdatedAtUtc = DateTimeOffset.UtcNow };
            _stateStore.SaveState(_state);
        }

        _stateStore.AppendAudit(new GuardAuditEntry
        {
            EventType = "COMMAND_EXECUTED",
            IdempotencyKey = request.IdempotencyKey,
            CallerRole = request.CallerRole.ToString(),
            CallerId = request.CallerId,
            Action = request.TargetProfile is not null ? $"{request.Command}:{request.TargetProfile}" : request.Command.ToString(),
            ResultCode = response.Code,
            Summary = response.Message,
            StateAfter = response.OperationalState
        });

        return response;
    }

    public void Dispose() => _operationLock.Dispose();
}
