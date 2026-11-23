using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalLLMManager.Guard.Cli;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service;
using LocalLLMManager.Guard.Service.Control;
using LocalLLMManager.Guard.Service.Discovery;
using LocalLLMManager.Guard.Service.Gate;
using LocalLLMManager.Guard.Service.Security;
using Xunit;

namespace LocalLLMManager.Guard.Tests;

public sealed partial class Phase4AGuardAcceptanceTests
{
    // SUITE C: Resource Safety Gate Comprehensive Verification (Tests #4, #5, #6, #7, #8 + UNKNOWN_OWNER + Missing Telemetry)
    // =========================================================================
    [Fact]
    public void Test_03_ResourceSafetyGate_Vram_Ram_Commit_Port_Sha256_UnknownOwner_And_MissingTelemetry()
    {
        if (!IntegrationFixturesAvailable)
        {
            Assert.Skip("Integration fixtures (llama-swap / fake-model) are not present in this runner environment.");
            return;
        }
        var realSha = GuardResourceSafetyGate.ComputeFileSha256Hex(FakeModelA);
        var profile = new GuardProfileSpec
        {
            ProfileName = "qwen36-real-gate-test",
            ModelId = "qwen36-35b-a3b",
            UpstreamPort = QwenPort,
            RuntimeExecutablePath = FakeModelA,
            ExpectedRuntimeSha256 = realSha,
            RequiredVramMiB = 11500,
            SafetyVramMarginMiB = 1024,
            RequiredRamMiB = 8192,
            SafetyRamMarginMiB = 1024,
            RequiredCommitMiB = 12288,
            SafetyCommitMarginMiB = 1024,
            IsIsolatedMockProfile = false
        };

        var healthyTelemetry = new HostHardwareTelemetry
        {
            GpuAvailable = true,
            GpuName = "RTX 4060 Ti 16GB (Test)",
            GpuTotalVramMiB = 16380,
            GpuUsedVramMiB = 900,
            GpuFreeVramMiB = 15480,
            GpuUtilizationPct = 4,
            RamAvailable = true,
            RamTotalMiB = 32768,
            RamAvailableMiB = 18000,
            CommitAvailable = true,
            CommitLimitMiB = 49152,
            CommitUsedMiB = 22000,
            CommitAvailableMiB = 27152
        };

        // 1. All checks pass
        var pass = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest { TargetProfile = profile, Telemetry = healthyTelemetry });
        Assert.True(pass.Allowed);
        Assert.Equal("ALLOWED_RESOURCES_VERIFIED", pass.Code);
        Assert.True(pass.RuntimeSha256Matched);

        // 2. Test #4: RAM Gate
        var ramFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry with { RamAvailableMiB = 4096 }
        });
        Assert.False(ramFail.Allowed);
        Assert.Equal("BLOCKED_INSUFFICIENT_RAM", ramFail.Code);

        // 3. Test #5: Windows Commit Gate
        var commitFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry with { CommitAvailableMiB = 5000 }
        });
        Assert.False(commitFail.Allowed);
        Assert.Equal("BLOCKED_INSUFFICIENT_COMMIT", commitFail.Code);

        // 4. VRAM Gate
        var vramFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry with { GpuFreeVramMiB = 8000, GpuUsedVramMiB = 8380 }
        });
        Assert.False(vramFail.Allowed);
        Assert.Equal("BLOCKED_INSUFFICIENT_VRAM", vramFail.Code);

        // 5. Missing Telemetry Fail-Closed
        var missingGpu = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry with { GpuAvailable = false, GpuFreeVramMiB = null }
        });
        Assert.False(missingGpu.Allowed);
        Assert.Equal("RESOURCE_STATUS_UNKNOWN", missingGpu.Code);

        // 6. Test #6: Port Conflict Gate
        var portFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry,
            TargetPortOccupiedByPid = 44556,
            RequireTargetPortFree = true
        });
        Assert.False(portFail.Allowed);
        Assert.Equal("BLOCKED_PORT_STILL_OCCUPIED", portFail.Code);

        // 7. Old Model Process Still Alive
        var oldPidFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry,
            OldModelPidStillRunning = 33445
        });
        Assert.False(oldPidFail.Allowed);
        Assert.Equal("BLOCKED_OLD_PROCESS_NOT_EXITED", oldPidFail.Code);

        // 8. Test #7: Runtime SHA-256 Mismatch
        var shaFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile with { ExpectedRuntimeSha256 = "0000000000000000000000000000000000000000000000000000000000000000" },
            Telemetry = healthyTelemetry
        });
        Assert.False(shaFail.Allowed);
        Assert.Equal("BLOCKED_RUNTIME_SHA256_MISMATCH", shaFail.Code);
        Assert.False(shaFail.RuntimeSha256Matched);

        // 9. UNKNOWN_OWNER Fail-Closed
        var unknownOwnerFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry,
            DiscoveredProcesses = [new DiscoveredModelProcess { ProcessId = 77889, ProcessName = "llama-server.exe", OwnershipClass = ProcessOwnershipClass.UNKNOWN_OWNER, OwnershipReason = "AccessRestricted" }]
        });
        Assert.False(unknownOwnerFail.Allowed);
        Assert.Equal("BLOCKED_UNKNOWN_OWNER", unknownOwnerFail.Code);

        // 10. EXTERNAL_CODEX_OWNED Block
        var codexOwnerFail = GuardResourceSafetyGate.Evaluate(new GuardResourceGateRequest
        {
            TargetProfile = profile,
            Telemetry = healthyTelemetry,
            DiscoveredProcesses = [new DiscoveredModelProcess { ProcessId = 77890, ProcessName = "llama-server.exe", ListeningPorts = [18083], OwnershipClass = ProcessOwnershipClass.EXTERNAL_CODEX_OWNED, OwnershipReason = "Codex ancestor detected" }]
        });
        Assert.False(codexOwnerFail.Allowed);
        Assert.Equal("BLOCKED_BY_EXTERNAL_GPU_OWNER", codexOwnerFail.Code);

        WriteJsonEvidence("phase4a_resource_gate_evidence.json", new
        {
            testSuite = "Phase 4A Resource Safety Gate Verification (Tests #4, #5, #6, #7, #8 + UNKNOWN_OWNER + Missing Telemetry)",
            executedAtUtc = DateTimeOffset.UtcNow,
            verifiedRuntimePath = FakeModelA,
            verifiedRuntimeSha256 = realSha,
            cases = new
            {
                healthyBaselineAllowed = pass,
                ramInsufficientBlocked = ramFail,
                commitInsufficientBlocked = commitFail,
                vramInsufficientBlocked = vramFail,
                missingTelemetryFailClosed = missingGpu,
                portConflictBlocked = portFail,
                oldProcessNotExitedBlocked = oldPidFail,
                runtimeSha256MismatchBlocked = shaFail,
                unknownOwnerFailClosed = unknownOwnerFail,
                externalCodexOwnedBlocked = codexOwnerFail
            },
            allPassed = true
        });
    }

    // =========================================================================
    // SUITE D: Lifecycle, Serialization, Idempotency, Auto-Rollback, Double Fault & Restart Recovery (Tests #1, #8, #11, #12, #13, #14, #15)
    // =========================================================================
    [Fact]
    public async Task Test_04_IndependentStartStop_Concurrency_Idempotency_Rollback_DoubleFault_And_RestartRecovery()
    {
        if (!IntegrationFixturesAvailable)
        {
            Assert.Skip("Integration fixtures (llama-swap / fake-model) are not present in this runner environment.");
            return;
        }
        var workDir = Path.Combine(Path.GetTempPath(), $"guard_p4a_rb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var stateDir = Path.Combine(workDir, "guard_state");
        Process? protectedSwap = null;
        GuardRuntimeHost? guardHost = null;
        GuardRuntimeHost? restartedGuard = null;
        Socket? blockerSocket = null;

        try
        {
            var internalAdminKey = "guard-internal-rb0123456789abcdef0123456789abcdef";
            var agentKey = "agent-inf-rb9876543210fedcba9876543210";
            var yaml = CreateLlamaSwapYaml(Path.Combine(workDir, "rb.yaml"), internalAdminKey, includeFaults: true);
            protectedSwap = await StartLlamaSwapAsync(InternalSwapPort, yaml);

            var pipeName = $"LocalLLMManager.Guard.P4A.Rb.{Guid.NewGuid():N}";
            GuardHostConfig MakeConfig() => new()
            {
                StateDirectory = stateDir,
                PipeName = pipeName,
                AgentFacadeListenPort = FacadePort,
                InternalLlamaSwapBaseUrl = $"http://127.0.0.1:{InternalSwapPort}",
                OverrideCredentials = new GuardCredentialSet(internalAdminKey, agentKey, DateTimeOffset.UtcNow),
                Profiles = BuildMockProfiles(),
                DiscoveryTargetImageNames = ["fake-model.exe", Path.GetFileName(FakeModelA), Path.GetFileName(FakeModelB)],
                DiscoveryPortRangeMin = BasePort,
                DiscoveryPortRangeMax = BasePort + 10,
                ManagedGatewayPidProvider = () => protectedSwap.Id
            };

            // Test #1: Independent Guard Start & Stop
            guardHost = GuardHostFactory.Build(MakeConfig());
            await guardHost.StartAsync();
            var pipe = new GuardPipeClient(pipeName);

            var initialStatus = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS, CallerRole = GuardCallerRole.CliClient });
            Assert.True(initialStatus.Succeeded);

            // Establish healthy baseline cyber-tiel
            var baselineSwitch = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.CliClient, TargetProfile = "cyber-tiel" });
            Assert.True(baselineSwitch.Succeeded);
            Assert.Equal("cyber-tiel", baselineSwitch.ActiveProfile);

            // Test #8: Illegal switch request rejection (unknown profile)
            var illegalSwitch = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "non-existent-model-xyz" });
            Assert.False(illegalSwitch.Succeeded);
            Assert.Equal("INVALID_TARGET_PROFILE", illegalSwitch.Code);

            // Test #12: Request Idempotency (same IdempotencyKey replays cached result without re-executing)
            var idemKey = $"idem-{Guid.NewGuid():N}";
            var firstCall = await pipe.SendAsync(new GuardCommandRequest { IdempotencyKey = idemKey, Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "qwen36" });
            var replayCall = await pipe.SendAsync(new GuardCommandRequest { IdempotencyKey = idemKey, Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "qwen36" });
            Assert.True(firstCall.Succeeded);
            Assert.False(firstCall.DeduplicatedReplay);
            Assert.True(replayCall.Succeeded);
            Assert.True(replayCall.DeduplicatedReplay);
            Assert.Equal(firstCall.Code, replayCall.Code);

            // Test #11: Concurrent switch serialization (2 simultaneous switches cleanly serialized by SemaphoreSlim(1,1))
            var conc1 = pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "inkling2" });
            var conc2 = pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.CliClient, TargetProfile = "cyber-tiel" });
            var concResults = await Task.WhenAll(conc1, conc2);
            Assert.All(concResults, r => Assert.True(r.Succeeded, r.Message));
            var postConcStatus = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS });
            Assert.Single(postConcStatus.Status!.RunningManagedModels); // Strictly 1 model running 鈥?zero dual occupancy!

            // Ensure baseline is cyber-tiel before rollback suite
            await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, TargetProfile = "cyber-tiel" });

            // Test #13: Failure Automatic Rollback (Startup, Health Check Timeout, and Inference 500)
            var rbStartup = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "fail-startup" });
            Assert.Equal(SwitchFailureClass.MODEL_STARTUP_FAILED, rbStartup.FailureClass);
            Assert.Equal(RollbackOutcome.ROLLBACK_SUCCEEDED, rbStartup.RollbackOutcome);
            Assert.Equal(GuardOperationalState.RUNNING_HEALTHY, rbStartup.OperationalState);
            Assert.Equal("cyber-tiel", rbStartup.ActiveProfile);

            var rbHealth = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "fail-health" });
            Assert.Equal(SwitchFailureClass.MODEL_HEALTH_CHECK_FAILED, rbHealth.FailureClass);
            Assert.Equal(RollbackOutcome.ROLLBACK_SUCCEEDED, rbHealth.RollbackOutcome);
            Assert.Equal(GuardOperationalState.RUNNING_HEALTHY, rbHealth.OperationalState);
            Assert.Equal("cyber-tiel", rbHealth.ActiveProfile);

            var rbInference = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "fail-inference" });
            Assert.Equal(SwitchFailureClass.MODEL_INFERENCE_FAILED, rbInference.FailureClass);
            Assert.Equal(RollbackOutcome.ROLLBACK_SUCCEEDED, rbInference.RollbackOutcome);
            Assert.Equal(GuardOperationalState.RUNNING_HEALTHY, rbInference.OperationalState);
            Assert.Equal("cyber-tiel", rbInference.ActiveProfile);

            // Test #14: Double Fault -> FAULT_LOCKED (block cyber-tiel port 19251 right before rollback restore!)
            guardHost.Coordinator.BeforeRollbackRestoreHookAsync = _ =>
            {
                blockerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                blockerSocket.Bind(new IPEndPoint(IPAddress.Loopback, CyberPort));
                blockerSocket.Listen(1);
                return Task.CompletedTask;
            };
            var doubleFault = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "fail-startup" });
            guardHost.Coordinator.BeforeRollbackRestoreHookAsync = null;

            Assert.Equal(SwitchFailureClass.MODEL_STARTUP_FAILED, doubleFault.FailureClass);
            Assert.Equal(RollbackOutcome.ROLLBACK_FAILED, doubleFault.RollbackOutcome);
            Assert.Equal(GuardOperationalState.FAULT_LOCKED, doubleFault.OperationalState);

            // Verify switches are rejected while FAULT_LOCKED
            var blockedWhileFaultLocked = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "qwen36" });
            Assert.False(blockedWhileFaultLocked.Succeeded);
            Assert.Equal("BLOCKED_FAULT_LOCKED", blockedWhileFaultLocked.Code);

            // Release blocker socket
            blockerSocket?.Dispose();
            blockerSocket = null;

            // Test #15: Guard Abnormal Stop & Restart State Recovery!
            await guardHost.DisposeAsync();
            guardHost = null;

            restartedGuard = GuardHostFactory.Build(MakeConfig());
            await restartedGuard.StartAsync();

            // Must recover persisted FAULT_LOCKED state & LastKnownGoodProfile across restart!
            var recoveredStatus = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS, CallerRole = GuardCallerRole.CliClient });
            Assert.Equal(GuardOperationalState.FAULT_LOCKED, recoveredStatus.OperationalState);
            Assert.Equal("cyber-tiel", recoveredStatus.LastKnownGoodProfile);

            // Operator explicitly unlocks fault -> recovers and switches cleanly to cyber-tiel!
            var unlocked = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.UNLOCK_FAULT, CallerRole = GuardCallerRole.CliClient, Reason = "Cleared blocked port 19251 after audit" });
            Assert.True(unlocked.Succeeded);
            Assert.NotEqual(GuardOperationalState.FAULT_LOCKED, unlocked.OperationalState);

            var recoveredSwitch = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.CliClient, TargetProfile = "cyber-tiel" });
            Assert.True(recoveredSwitch.Succeeded);
            Assert.Equal(GuardOperationalState.RUNNING_HEALTHY, recoveredSwitch.OperationalState);

            var auditTail = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_AUDIT_TAIL, CallerRole = GuardCallerRole.CliClient });

            WriteJsonEvidence("phase4a_rollback_evidence.json", new
            {
                testSuite = "Phase 4A Lifecycle, Serialization, Idempotency, Auto-Rollback, Double Fault & Restart Recovery (Tests #1, #8, #11, #12, #13, #14, #15)",
                executedAtUtc = DateTimeOffset.UtcNow,
                independentStartStopVerified = true,
                illegalProfileRejected = new { illegalSwitch.Succeeded, illegalSwitch.Code, illegalSwitch.Message },
                idempotencyVerified = new { idempotencyKey = idemKey, firstReplay = firstCall.DeduplicatedReplay, secondReplay = replayCall.DeduplicatedReplay, code = replayCall.Code },
                concurrentSwitchSerialization = new
                {
                    bothSucceeded = concResults.All(r => r.Succeeded),
                    runningModelCountAfter = postConcStatus.Status!.RunningManagedModels.Count,
                    runningModelsAfter = postConcStatus.Status.RunningManagedModels
                },
                rollbackCases = new
                {
                    startupFailureAutoRollback = new { rbStartup.FailureClass, rbStartup.RollbackOutcome, rbStartup.OperationalState, rbStartup.ActiveProfile, rbStartup.Message },
                    healthCheckFailureAutoRollback = new { rbHealth.FailureClass, rbHealth.RollbackOutcome, rbHealth.OperationalState, rbHealth.ActiveProfile, rbHealth.Message },
                    inferenceFailureAutoRollback = new { rbInference.FailureClass, rbInference.RollbackOutcome, rbInference.OperationalState, rbInference.ActiveProfile, rbInference.Message },
                    doubleFaultLocked = new { doubleFault.FailureClass, doubleFault.RollbackOutcome, doubleFault.OperationalState, doubleFault.Code, doubleFault.Message },
                    switchBlockedWhileFaultLocked = new { blockedWhileFaultLocked.Succeeded, blockedWhileFaultLocked.Code }
                },
                restartRecoveryVerified = new
                {
                    recoveredStateAfterRestart = recoveredStatus.OperationalState,
                    recoveredLastKnownGoodProfile = recoveredStatus.LastKnownGoodProfile,
                    unlockCode = unlocked.Code,
                    postRecoverySwitchSucceeded = recoveredSwitch.Succeeded,
                    postRecoveryState = recoveredSwitch.OperationalState
                },
                auditEntryCount = auditTail.AuditEntries?.Count ?? 0,
                allPassed = true
            });
        }
        finally
        {
            blockerSocket?.Dispose();
            if (guardHost is not null) await guardHost.DisposeAsync();
            if (restartedGuard is not null) await restartedGuard.DisposeAsync();
            StopProcessSafely(protectedSwap);
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private sealed record RawHttpResult(int StatusCode, string Body);

    private static async Task<RawHttpResult> SendRawAsync(HttpClient client, HttpMethod method, string url, string? bearerKey, string? jsonBody)
    {
        using var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearerKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        return new RawHttpResult((int)resp.StatusCode, body);
    }

    private sealed record SseStreamResult(
        string RequestId,
        int StatusCode,
        bool SawDone,
        int WordCount,
        string LoadingBanner,
        DateTimeOffset SubmittedAtUtc,
        DateTimeOffset? FirstContentAtUtc,
        DateTimeOffset CompletedAtUtc);

    private static async Task<SseStreamResult> StreamSseThroughFacadeAsync(string url, string agentKey, string reqId, Action? onFirstContent = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model = "local-coder", stream = true, messages = new[] { new { role = "user", content = $"SSE FIFO test {reqId}" } } }),
            Encoding.UTF8, "application/json");

        var submitted = DateTimeOffset.UtcNow;
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var status = (int)resp.StatusCode;
        var loadingSb = new StringBuilder();
        var contentSb = new StringBuilder();
        DateTimeOffset? firstContent = null;
        bool sawDone = false;

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var val = line[5..].Trim();
            if (val == "[DONE]") { sawDone = true; break; }
            try
            {
                using var doc = JsonDocument.Parse(val);
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                {
                    var delta = ch[0].GetProperty("delta");
                    if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        loadingSb.Append(rc.GetString());
                    if (delta.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
                    {
                        var text = ct.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (firstContent is null)
                            {
                                firstContent = DateTimeOffset.UtcNow;
                                onFirstContent?.Invoke();
                            }
                            contentSb.Append(text);
                        }
                    }
                }
            }
            catch { }
        }

        var words = contentSb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return new SseStreamResult(reqId, status, sawDone, words, loadingSb.ToString(), submitted, firstContent, DateTimeOffset.UtcNow);
    }
}
