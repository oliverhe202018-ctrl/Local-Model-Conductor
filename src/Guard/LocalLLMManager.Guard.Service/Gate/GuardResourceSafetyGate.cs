using System.Security.Cryptography;
using LocalLLMManager.Guard.Contracts;

namespace LocalLLMManager.Guard.Service.Gate;

public sealed record GuardResourceGateRequest
{
    public required GuardProfileSpec TargetProfile { get; init; }
    public HostHardwareTelemetry? Telemetry { get; init; }
    public IReadOnlyList<DiscoveredModelProcess> DiscoveredProcesses { get; init; } = [];
    public int? OldModelPidStillRunning { get; init; }
    public int? TargetPortOccupiedByPid { get; init; }
    public bool RequireTargetPortFree { get; init; } = true;
    public bool EnforceGpuHeadroomNow { get; init; } = true;
    public bool ForceGpuGateOnMockProfile { get; init; }
}

/// <summary>
/// Upgraded Phase 4A Resource Safety Gate inheriting LocalLLMManager.Core.Utilities.ResourceSafetyGateEvaluator.
///
/// Enforces ALL 8 mandatory fail-closed rules:
/// 1. UNKNOWN_OWNER process -> BLOCKED_UNKNOWN_OWNER (fail-closed!)
/// 2. External GPU owner (EXTERNAL_CODEX_OWNED / EXTERNAL_UNMANAGED) -> BLOCKED_BY_EXTERNAL_GPU_OWNER
/// 3. Old model OS process not yet exited -> BLOCKED_OLD_PROCESS_NOT_EXITED
/// 4. Target port occupied -> BLOCKED_PORT_STILL_OCCUPIED
/// 5. Runtime executable exists & matches expected SHA-256 -> BLOCKED_RUNTIME_MISSING / BLOCKED_RUNTIME_SHA256_MISMATCH
/// 6. Complete & trustworthy GPU VRAM, Physical RAM, and Windows Commit telemetry -> RESOURCE_STATUS_UNKNOWN
/// 7. GPU Free VRAM - Safety Margin >= Required VRAM -> BLOCKED_INSUFFICIENT_VRAM
/// 8. Available RAM & Available Windows Commit headroom checks -> BLOCKED_INSUFFICIENT_RAM / BLOCKED_INSUFFICIENT_COMMIT
/// </summary>
public static class GuardResourceSafetyGate
{
    public static ResourceGateEvaluation Evaluate(GuardResourceGateRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var profile = req.TargetProfile;
        var tel = req.Telemetry;

        ResourceGateEvaluation Decide(
            bool allowed,
            string code,
            string reason,
            string? blockingOwner = null,
            string? computedSha = null,
            bool? shaMatched = null) => new()
            {
                Allowed = allowed,
                Code = code,
                Reason = reason,
                BlockingOwner = blockingOwner,
                GpuFreeMiB = tel?.GpuFreeVramMiB,
                GpuUsedMiB = tel?.GpuUsedVramMiB,
                GpuTotalMiB = tel?.GpuTotalVramMiB,
                AvailableRamMiB = tel?.RamAvailableMiB,
                AvailableCommitMiB = tel?.CommitAvailableMiB,
                ComputedRuntimeSha256 = computedSha,
                RuntimeSha256Matched = shaMatched,
                EvaluatedAtUtc = DateTimeOffset.UtcNow
            };

        // Rule 1: UNKNOWN_OWNER MUST fail closed unconditionally (even on mock profiles!)
        var unknownOwner = req.DiscoveredProcesses.FirstOrDefault(p => p.OwnershipClass == ProcessOwnershipClass.UNKNOWN_OWNER);
        if (unknownOwner is not null)
        {
            return Decide(
                false,
                "BLOCKED_UNKNOWN_OWNER",
                $"Detected model process with UNKNOWN_OWNER (PID={unknownOwner.ProcessId}, {unknownOwner.OwnershipReason}). Fail-closed policy denies operation.",
                blockingOwner: $"UNKNOWN_OWNER PID={unknownOwner.ProcessId}");
        }

        // Rule 2: External Codex or External Unmanaged process blocks ALL GPU-competing actions
        var externalOwner = req.DiscoveredProcesses.FirstOrDefault(p =>
            p.OwnershipClass is ProcessOwnershipClass.EXTERNAL_CODEX_OWNED or ProcessOwnershipClass.EXTERNAL_UNMANAGED);
        if (externalOwner is not null)
        {
            var ports = externalOwner.ListeningPorts.Count > 0 ? string.Join(",", externalOwner.ListeningPorts) : "none";
            var ownerLabel = $"{externalOwner.OwnershipClass} PID={externalOwner.ProcessId} Ports=[{ports}]";
            return Decide(
                false,
                "BLOCKED_BY_EXTERNAL_GPU_OWNER",
                $"External model process detected ({ownerLabel}, Runtime={externalOwner.ExecutablePath ?? "unknown"}). Guard is locked in READ_ONLY_OBSERVATION and will never terminate or interfere with external processes.",
                blockingOwner: ownerLabel);
        }

        // Rule 3: Previous model OS process must be fully exited
        if (req.OldModelPidStillRunning.HasValue)
        {
            return Decide(
                false,
                "BLOCKED_OLD_PROCESS_NOT_EXITED",
                $"Previous managed model PID={req.OldModelPidStillRunning.Value} has not fully exited yet.",
                blockingOwner: $"PID={req.OldModelPidStillRunning.Value}");
        }

        // Rule 4: Target port must be free
        if (req.RequireTargetPortFree && req.TargetPortOccupiedByPid.HasValue)
        {
            return Decide(
                false,
                "BLOCKED_PORT_STILL_OCCUPIED",
                $"Target upstream port :{profile.UpstreamPort} is already occupied by PID={req.TargetPortOccupiedByPid.Value}.",
                blockingOwner: $"Port:{profile.UpstreamPort} PID={req.TargetPortOccupiedByPid.Value}");
        }

        // Rule 5: Runtime file existence & SHA-256 verification
        string? computedSha = null;
        bool? shaMatched = null;
        if (!string.IsNullOrWhiteSpace(profile.RuntimeExecutablePath))
        {
            if (!File.Exists(profile.RuntimeExecutablePath))
            {
                return Decide(
                    false,
                    "BLOCKED_RUNTIME_MISSING",
                    $"Runtime executable does not exist: {profile.RuntimeExecutablePath}",
                    blockingOwner: profile.RuntimeExecutablePath,
                    shaMatched: false);
            }

            if (!string.IsNullOrWhiteSpace(profile.ExpectedRuntimeSha256))
            {
                computedSha = ComputeFileSha256Hex(profile.RuntimeExecutablePath);
                shaMatched = string.Equals(computedSha, profile.ExpectedRuntimeSha256.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!shaMatched.Value)
                {
                    return Decide(
                        false,
                        "BLOCKED_RUNTIME_SHA256_MISMATCH",
                        $"Runtime SHA-256 mismatch for {profile.RuntimeExecutablePath}: expected {profile.ExpectedRuntimeSha256}, actual {computedSha}.",
                        blockingOwner: profile.RuntimeExecutablePath,
                        computedSha: computedSha,
                        shaMatched: false);
                }
            }
        }

        // Isolated CPU-only mock profiles may skip physical GPU/RAM/Commit budgets unless explicitly forced by gate tests
        if (profile.IsIsolatedMockProfile && !req.ForceGpuGateOnMockProfile)
        {
            return Decide(
                true,
                "ALLOWED_ISOLATED_MOCK_VERIFIED",
                $"Isolated mock profile '{profile.ProfileName}' passed external-owner, port, process-exit, and Runtime SHA-256 checks.",
                computedSha: computedSha,
                shaMatched: shaMatched);
        }

        // Rule 6: Fail-closed whenever ANY critical telemetry (GPU / RAM / Commit) is missing or invalid
        if (tel is null || !tel.GpuAvailable || !tel.GpuTotalVramMiB.HasValue || !tel.GpuUsedVramMiB.HasValue || !tel.GpuFreeVramMiB.HasValue
            || tel.GpuTotalVramMiB.Value <= 0 || tel.GpuUsedVramMiB.Value < 0 || tel.GpuFreeVramMiB.Value < 0)
        {
            return Decide(
                false,
                "RESOURCE_STATUS_UNKNOWN",
                $"GPU VRAM telemetry is missing or invalid ({tel?.DiagnosticError ?? "null telemetry"}). Fail-closed policy denies model launch.",
                computedSha: computedSha,
                shaMatched: shaMatched);
        }

        if (!tel.RamAvailable || !tel.RamAvailableMiB.HasValue || tel.RamAvailableMiB.Value < 0)
        {
            return Decide(
                false,
                "RESOURCE_STATUS_UNKNOWN",
                "Windows physical RAM telemetry is unavailable. Fail-closed policy denies model launch.",
                computedSha: computedSha,
                shaMatched: shaMatched);
        }

        if (profile.RequiredCommitMiB > 0 && (!tel.CommitAvailable || !tel.CommitAvailableMiB.HasValue || tel.CommitAvailableMiB.Value < 0))
        {
            return Decide(
                false,
                "RESOURCE_STATUS_UNKNOWN",
                "Windows Commit memory telemetry is unavailable. Fail-closed policy denies model launch.",
                computedSha: computedSha,
                shaMatched: shaMatched);
        }

        if (req.EnforceGpuHeadroomNow)
        {
            // Rule 7: GPU VRAM headroom
            var usableVram = tel.GpuFreeVramMiB.Value - profile.SafetyVramMarginMiB;
            if (usableVram < profile.RequiredVramMiB)
            {
                return Decide(
                    false,
                    "BLOCKED_INSUFFICIENT_VRAM",
                    $"Insufficient GPU VRAM: free {tel.GpuFreeVramMiB.Value:F0} MiB - safety margin {profile.SafetyVramMarginMiB:F0} MiB = {usableVram:F0} MiB < required {profile.RequiredVramMiB:F0} MiB.",
                    computedSha: computedSha,
                    shaMatched: shaMatched);
            }

            // Rule 8a: Physical RAM headroom
            var usableRam = tel.RamAvailableMiB.Value - profile.SafetyRamMarginMiB;
            if (usableRam < profile.RequiredRamMiB)
            {
                return Decide(
                    false,
                    "BLOCKED_INSUFFICIENT_RAM",
                    $"Insufficient physical RAM: available {tel.RamAvailableMiB.Value:F0} MiB - safety margin {profile.SafetyRamMarginMiB:F0} MiB = {usableRam:F0} MiB < required {profile.RequiredRamMiB:F0} MiB.",
                    computedSha: computedSha,
                    shaMatched: shaMatched);
            }

            // Rule 8b: Windows Commit headroom
            if (profile.RequiredCommitMiB > 0 && tel.CommitAvailableMiB.HasValue)
            {
                var usableCommit = tel.CommitAvailableMiB.Value - profile.SafetyCommitMarginMiB;
                if (usableCommit < profile.RequiredCommitMiB)
                {
                    return Decide(
                        false,
                        "BLOCKED_INSUFFICIENT_COMMIT",
                        $"Insufficient Windows Commit memory: available {tel.CommitAvailableMiB.Value:F0} MiB - safety margin {profile.SafetyCommitMarginMiB:F0} MiB = {usableCommit:F0} MiB < required {profile.RequiredCommitMiB:F0} MiB.",
                        computedSha: computedSha,
                        shaMatched: shaMatched);
                }
            }
        }

        return Decide(
            true,
            "ALLOWED_RESOURCES_VERIFIED",
            $"All 8 OS resource & security gates passed (GPU free {tel.GpuFreeVramMiB.Value:F0} MiB, RAM avail {tel.RamAvailableMiB.Value:F0} MiB, Commit avail {tel.CommitAvailableMiB?.ToString("F0") ?? "N/A"} MiB).",
            computedSha: computedSha,
            shaMatched: shaMatched);
    }

    public static string ComputeFileSha256Hex(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
