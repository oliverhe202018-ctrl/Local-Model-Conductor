using System.Diagnostics;
using System.Net.Sockets;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Discovery;
using LocalLLMManager.Guard.Service.Gate;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Control;

public sealed partial class GuardControlCoordinator
{
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

}
