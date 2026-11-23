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
public sealed class GuardControlCoordinator : IDisposable
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

    private async Task<GuardCommandResponse> ExecuteSwitchProfileLockedAsync(GuardCommandRequest request, CancellationToken ct)
    {
        var targetName = request.TargetProfile?.Trim();
        if (string.IsNullOrWhiteSpace(targetName) || !_options.Profiles.TryGetValue(targetName, out var targetSpec))
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "INVALID_TARGET_PROFILE",
                Message = $"Target profile '{targetName}' is not registered in Guard.",
                OperationalState = _state.OperationalState,
                FailureClass = SwitchFailureClass.DENIED_BY_GATE,
                ActiveProfile = _state.ActiveProfile,
                LastKnownGoodProfile = _state.LastKnownGoodProfile
            };
        }

        if (_state.OperationalState == GuardOperationalState.FAULT_LOCKED || _state.OperationalState == GuardOperationalState.RECOVERY_REQUIRED)
        {
            var isRecovery = _state.OperationalState == GuardOperationalState.RECOVERY_REQUIRED;
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = isRecovery ? "BLOCKED_RECOVERY_REQUIRED" : "BLOCKED_FAULT_LOCKED",
                Message = isRecovery
                    ? $"Guard is in RECOVERY_REQUIRED state ({_state.FaultLockReason}). Operator must inspect logs and issue UNLOCK_FAULT first."
                    : $"Guard is in FAULT_LOCKED state ({_state.FaultLockReason}). Operator must inspect preserved logs and issue UNLOCK_FAULT first.",
                OperationalState = _state.OperationalState,
                FailureClass = SwitchFailureClass.DENIED_BY_GATE,
                ActiveProfile = _state.ActiveProfile,
                LastKnownGoodProfile = _state.LastKnownGoodProfile
            };
        }

        // Refresh live observation (OS discovery + hardware telemetry + live llama-swap state)
        var obs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        if (!obs.GatewayHealthy)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "GATEWAY_UNREACHABLE",
                Message = "Internal llama-swap v275 gateway is unreachable.",
                OperationalState = _state.OperationalState,
                FailureClass = SwitchFailureClass.GATEWAY_UNREACHABLE,
                Status = obs
            };
        }

        // Duplicate command protection: if already active and running healthy, no-op cleanly!
        if (string.Equals(obs.ActiveProfile, targetName, StringComparison.OrdinalIgnoreCase) &&
            obs.RunningManagedModels.Contains(targetSpec.ModelId, StringComparer.OrdinalIgnoreCase) &&
            !obs.ExternalGpuConflictActive)
        {
            UpdateState(GuardOperationalState.RUNNING_HEALTHY, targetName, targetName, null);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = true,
                IdempotencyKey = request.IdempotencyKey,
                Code = "ALREADY_ACTIVE_PROFILE",
                Message = $"Profile '{targetName}' ({targetSpec.ModelId}) is already active and healthy.",
                OperationalState = GuardOperationalState.RUNNING_HEALTHY,
                ActiveProfile = targetName,
                LastKnownGoodProfile = targetName,
                Status = obs
            };
        }

        // Check target port occupancy (only flag if occupied by a process NOT already running targetSpec.ModelId)
        var targetPortPid = GetOccupiedPortPid(targetSpec.UpstreamPort);
        var gateEval = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = targetSpec,
            Telemetry = obs.Telemetry,
            DiscoveredProcesses = obs.DiscoveredProcesses,
            TargetPortOccupiedByPid = targetPortPid,
            RequireTargetPortFree = true,
            EnforceGpuHeadroomNow = true,
            ForceGpuGateOnMockProfile = request.ForceGpuGateEvaluationOnMock
        });

        if (!gateEval.Allowed)
        {
            var newState = gateEval.Code is "BLOCKED_BY_EXTERNAL_GPU_OWNER" or "BLOCKED_UNKNOWN_OWNER"
                ? GuardOperationalState.READ_ONLY_OBSERVATION
                : _state.OperationalState;
            UpdateState(newState, obs.ActiveProfile, _state.LastKnownGoodProfile ?? obs.ActiveProfile, _state.FaultLockReason);
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = gateEval.Code,
                Message = gateEval.Reason,
                OperationalState = newState,
                FailureClass = SwitchFailureClass.DENIED_BY_GATE,
                ActiveProfile = obs.ActiveProfile,
                LastKnownGoodProfile = _state.LastKnownGoodProfile ?? obs.ActiveProfile,
                GateEvaluation = gateEval,
                Status = obs
            };
        }

        var previousGoodProfile = _state.LastKnownGoodProfile ?? obs.ActiveProfile;

        // If inference was paused, resume before switching
        if (obs.InferencePaused)
            await _llamaSwap.ResumeInferenceAsync(ct).ConfigureAwait(false);

        // Step 1: Update active profile pin via llama-swap PUT /api/profiles/active (ZERO Gateway restart!)
        if (obs.InFlightRequestCount > 0)
            UpdateState(GuardOperationalState.DRAINING, targetName, previousGoodProfile, null);
        else
            UpdateState(GuardOperationalState.LOADING, targetName, previousGoodProfile, null);

        var (pinOk, pinStatus, pinBody) = await _llamaSwap.SetActiveProfileAsync(targetName, ct).ConfigureAwait(false);
        if (!pinOk)
        {
            return new GuardCommandResponse
            {
                Accepted = false,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "PROFILE_PIN_FAILED",
                Message = $"llama-swap rejected PUT /api/profiles/active '{targetName}' (HTTP {pinStatus}): {pinBody}",
                OperationalState = GuardOperationalState.ALLOW_SWITCH,
                FailureClass = SwitchFailureClass.GATEWAY_UNREACHABLE,
                ActiveProfile = obs.ActiveProfile,
                LastKnownGoodProfile = previousGoodProfile
            };
        }

        // Step 2: Controlled smoke inference on fixed local-coder triggers FIFO drain (if busy) -> unload old -> load target -> health -> inference!
        UpdateState(GuardOperationalState.LOADING, targetName, previousGoodProfile, null);
        var smoke = await _llamaSwap.RunControlledSmokeInferenceAsync(
            _options.StableLogicalModelId,
            TimeSpan.FromSeconds(Math.Clamp(_options.DrainTimeoutSeconds + 15, 10, 90)),
            ct).ConfigureAwait(false);

        if (smoke.Succeeded)
        {
            UpdateState(GuardOperationalState.RUNNING_HEALTHY, targetName, targetName, null);
            var afterStatus = await RefreshObservationAsync(ct).ConfigureAwait(false);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = true,
                IdempotencyKey = request.IdempotencyKey,
                Code = "SWITCH_SUCCEEDED_INFERENCE_VERIFIED",
                Message = $"Switched local-coder -> profile '{targetName}' ({targetSpec.ModelId}) and verified HTTP 200 inference in {smoke.ElapsedMs} ms.",
                OperationalState = GuardOperationalState.RUNNING_HEALTHY,
                FailureClass = SwitchFailureClass.NONE,
                RollbackOutcome = RollbackOutcome.NOT_TRIGGERED,
                ActiveProfile = targetName,
                LastKnownGoodProfile = targetName,
                GateEvaluation = gateEval,
                Status = afterStatus
            };
        }

        // Step 3: Classify failure stage accurately
        var failureClass = ClassifySmokeFailure(smoke);
        _stateStore.AppendAudit(new GuardAuditEntry
        {
            EventType = "TARGET_SWITCH_FAILED",
            IdempotencyKey = request.IdempotencyKey,
            CallerRole = request.CallerRole.ToString(),
            CallerId = request.CallerId,
            Action = $"SWITCH_PROFILE:{targetName}",
            ResultCode = failureClass.ToString(),
            Summary = $"Target '{targetName}' failed ({failureClass}, HTTP {smoke.StatusCode}, src={smoke.ErrorSource}): {smoke.ErrorMessage}",
            StateAfter = GuardOperationalState.ROLLING_BACK
        });

        // Step 4: 7-Step Automatic Rollback via llama-swap admin API (Guard NEVER kills llama-server directly!)
        return await ExecuteAutomaticRollbackLockedAsync(request, targetSpec, previousGoodProfile, failureClass, smoke, ct).ConfigureAwait(false);
    }

    private async Task<GuardCommandResponse> ExecuteAutomaticRollbackLockedAsync(
        GuardCommandRequest request,
        GuardProfileSpec failedTarget,
        string? rollbackProfileName,
        SwitchFailureClass failureClass,
        LlamaSwapSmokeResult failedSmoke,
        CancellationToken ct)
    {
        UpdateState(GuardOperationalState.ROLLING_BACK, failedTarget.ProfileName, rollbackProfileName, null);

        // 4.1 Unload via llama-swap management API (NEVER Process.Kill!)
        await _llamaSwap.UnloadAllAsync(ct).ConfigureAwait(false);

        // 4.2 Confirm failed target process exited & target port released
        var portReleased = await WaitForPortReleasedAsync(failedTarget.UpstreamPort, _options.PortReleaseWaitMs, ct).ConfigureAwait(false);

        // 4.3 Verify rollback target exists
        if (string.IsNullOrWhiteSpace(rollbackProfileName) || !_options.Profiles.TryGetValue(rollbackProfileName, out var rollbackSpec))
        {
            var reason = $"Target '{failedTarget.ProfileName}' failed ({failureClass}) and no valid previous profile exists for rollback.";
            UpdateState(GuardOperationalState.FAULT_LOCKED, null, rollbackProfileName, reason);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "ROLLBACK_FAILED_NO_BASELINE",
                Message = reason,
                OperationalState = GuardOperationalState.FAULT_LOCKED,
                FailureClass = failureClass,
                RollbackOutcome = RollbackOutcome.ROLLBACK_FAILED,
                LastKnownGoodProfile = rollbackProfileName
            };
        }

        if (BeforeRollbackRestoreHookAsync is not null)
            await BeforeRollbackRestoreHookAsync(rollbackProfileName).ConfigureAwait(false);

        // 4.4 Re-check Resource Gate for rollback target (port free, external clean, runtime SHA-256)
        var preRollbackObs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        var rbPortPid = GetOccupiedPortPid(rollbackSpec.UpstreamPort);
        var rbGate = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = rollbackSpec,
            Telemetry = preRollbackObs.Telemetry,
            DiscoveredProcesses = preRollbackObs.DiscoveredProcesses,
            TargetPortOccupiedByPid = rbPortPid,
            RequireTargetPortFree = true,
            EnforceGpuHeadroomNow = true,
            ForceGpuGateOnMockProfile = request.ForceGpuGateEvaluationOnMock
        });

        if (!portReleased || !rbGate.Allowed)
        {
            var reason = $"DOUBLE FAULT: Target '{failedTarget.ProfileName}' failed ({failureClass}) AND rollback gate to '{rollbackProfileName}' failed (portReleased={portReleased}, gate={rbGate.Code}: {rbGate.Reason}).";
            UpdateState(GuardOperationalState.FAULT_LOCKED, null, rollbackProfileName, reason);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "DOUBLE_FAULT_ROLLBACK_GATE_DENIED",
                Message = reason,
                OperationalState = GuardOperationalState.FAULT_LOCKED,
                FailureClass = failureClass,
                RollbackOutcome = RollbackOutcome.ROLLBACK_FAILED,
                LastKnownGoodProfile = rollbackProfileName,
                GateEvaluation = rbGate,
                Status = preRollbackObs
            };
        }

        // 4.5 Restore old profile via llama-swap PUT /api/profiles/active
        var (rbPinOk, _, _) = await _llamaSwap.SetActiveProfileAsync(rollbackProfileName, ct).ConfigureAwait(false);
        if (!rbPinOk)
        {
            var reason = $"DOUBLE FAULT: Target '{failedTarget.ProfileName}' failed ({failureClass}) AND restoring profile '{rollbackProfileName}' via llama-swap failed.";
            UpdateState(GuardOperationalState.FAULT_LOCKED, null, rollbackProfileName, reason);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = false,
                IdempotencyKey = request.IdempotencyKey,
                Code = "DOUBLE_FAULT_ROLLBACK_PIN_FAILED",
                Message = reason,
                OperationalState = GuardOperationalState.FAULT_LOCKED,
                FailureClass = failureClass,
                RollbackOutcome = RollbackOutcome.ROLLBACK_FAILED,
                LastKnownGoodProfile = rollbackProfileName
            };
        }

        // 4.6 Verify old model health & real HTTP 200 inference on local-coder
        var rbSmoke = await _llamaSwap.RunControlledSmokeInferenceAsync(_options.StableLogicalModelId, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var postObs = await RefreshObservationAsync(ct).ConfigureAwait(false);
        var dualOccupancy = postObs.RunningManagedModels.Count > 1;

        if (rbSmoke.Succeeded && !dualOccupancy)
        {
            // 4.7 Rollback succeeded -> clear fault lock and restore RUNNING_HEALTHY!
            UpdateState(GuardOperationalState.RUNNING_HEALTHY, rollbackProfileName, rollbackProfileName, null);
            return new GuardCommandResponse
            {
                Accepted = true,
                Succeeded = false, // Target switch failed, though rollback succeeded!
                IdempotencyKey = request.IdempotencyKey,
                Code = "TARGET_FAILED_AUTO_ROLLBACK_SUCCEEDED",
                Message = $"Target '{failedTarget.ProfileName}' failed ({failureClass}: {failedSmoke.ErrorMessage}); automatically rolled back and verified '{rollbackProfileName}' (HTTP 200, dualGpu=false).",
                OperationalState = GuardOperationalState.RUNNING_HEALTHY,
                FailureClass = failureClass,
                RollbackOutcome = RollbackOutcome.ROLLBACK_SUCCEEDED,
                ActiveProfile = rollbackProfileName,
                LastKnownGoodProfile = rollbackProfileName,
                GateEvaluation = rbGate,
                Status = postObs
            };
        }

        // DOUBLE FAULT -> FAULT_LOCKED (NEVER fake recovery!)
        var doubleFaultReason = $"DOUBLE FAULT: Target '{failedTarget.ProfileName}' failed ({failureClass}) AND rollback recovery to '{rollbackProfileName}' ALSO failed (HTTP {rbSmoke.StatusCode}: {rbSmoke.ErrorMessage}, dualOccupancy={dualOccupancy}).";
        UpdateState(GuardOperationalState.FAULT_LOCKED, null, rollbackProfileName, doubleFaultReason);
        return new GuardCommandResponse
        {
            Accepted = true,
            Succeeded = false,
            IdempotencyKey = request.IdempotencyKey,
            Code = "DOUBLE_FAULT_ROLLBACK_FAILED",
            Message = doubleFaultReason,
            OperationalState = GuardOperationalState.FAULT_LOCKED,
            FailureClass = failureClass,
            RollbackOutcome = RollbackOutcome.ROLLBACK_FAILED,
            ActiveProfile = null,
            LastKnownGoodProfile = rollbackProfileName,
            GateEvaluation = rbGate,
            Status = postObs
        };
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

    private static SwitchFailureClass ClassifySmokeFailure(LlamaSwapSmokeResult smoke)
    {
        var msg = (smoke.ErrorMessage ?? string.Empty).ToLowerInvariant();
        if (smoke.StatusCode == 504 || string.Equals(smoke.ErrorSource, "guard-timeout", StringComparison.OrdinalIgnoreCase))
            return SwitchFailureClass.DRAIN_TIMEOUT;
        if (msg.Contains("health check timed out") || msg.Contains("deadline exceeded"))
            return SwitchFailureClass.MODEL_HEALTH_CHECK_FAILED;
        if (msg.Contains("exited prematurely") || msg.Contains("unable to start") || msg.Contains("failed to start"))
            return SwitchFailureClass.MODEL_STARTUP_FAILED;
        if (!string.Equals(smoke.ErrorSource, "llama-swap", StringComparison.OrdinalIgnoreCase) && smoke.StatusCode >= 500)
            return SwitchFailureClass.MODEL_INFERENCE_FAILED;
        return SwitchFailureClass.MODEL_STARTUP_FAILED;
    }

    private int? GetOccupiedPortPid(int port)
    {
        if (PortOccupiedOverride is not null)
            return PortOccupiedOverride(port);

        var listeners = WindowsTcpPortTable.GetAllListeningTcpPorts();
        var match = listeners.FirstOrDefault(l => l.Port == port);
        return match?.ProcessId;
    }

    private async Task<bool> WaitForPortReleasedAsync(int port, int maxWaitMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        while (DateTimeOffset.UtcNow <= deadline)
        {
            if (GetOccupiedPortPid(port) is null)
                return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return GetOccupiedPortPid(port) is null;
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
