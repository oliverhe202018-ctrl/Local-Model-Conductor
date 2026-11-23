using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service;
using LocalLLMManager.Guard.Service.Gate;
using LocalLLMManager.Guard.Service.Security;
using LocalLlmConsole.ControlCli;
using LocalLlmConsole.Models;
using LocalLlmConsole.Services;

namespace LocalLlmConsole.Tests;

public sealed class Phase4BGuardIntegrationTests
{
    private static readonly string ForkOutDir = ResolveForkOutDir();
    private static readonly string LlamaSwapExe = Environment.GetEnvironmentVariable("LLAMA_SWAP_PATH") ?? @"D:\project\本地模型计算\LocalLLMManager\vendor\llama-swap\v275\llama-swap.exe";
    private static readonly string FakeModelA = ResolveFakeModel(@"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275\fake-model.exe", "fake-model-a.exe");
    private static readonly string FakeModelB = ResolveFakeModel(@"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275-runtime-b\fake-model.exe", "fake-model-b.exe");
    private static readonly string LlwmctlExe = ResolveLlwmctlExe();
    private static readonly string WmAppExe = ResolveWmAppExe();

    private static string ResolveForkOutDir()
    {
        var dev = @"D:\project\本地模型计算\phase4b_windows_manager_fork";
        if (Directory.Exists(dev)) return dev;
        var local = Path.Combine(AppContext.BaseDirectory, "phase4b_windows_manager_fork");
        Directory.CreateDirectory(local);
        return local;
    }

    private static string ResolveFakeModel(string preferredPath, string fileName)
    {
        if (File.Exists(preferredPath)) return preferredPath;
        var inRepo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\tests\LocalLlmConsole.FakeRuntime\bin\Release\net10.0-windows\LocalLlmConsole.FakeRuntime.exe"));
        if (File.Exists(inRepo)) return inRepo;
        var fallback = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(fallback)) File.WriteAllBytes(fallback, [0x4D, 0x5A, 0x90, 0x00]);
        return fallback;
    }

    private static string ResolveLlwmctlExe()
    {
        var inRepo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\LocalLlmConsole.ControlCli\bin\Release\net10.0-windows\win-x64\llwmctl.exe"));
        if (File.Exists(inRepo)) return inRepo;
        var hardcoded = @"D:\project\本地模型计算\phase4b_windows_manager_fork\repo\src\LocalLlmConsole.ControlCli\bin\Release\net10.0-windows\win-x64\llwmctl.exe";
        if (File.Exists(hardcoded)) return hardcoded;
        return inRepo;
    }

    private static string ResolveWmAppExe()
    {
        var inRepo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\LocalLlmConsole.App\bin\Release\net10.0-windows\win-x64\LlamaCppWindowsManager.exe"));
        if (File.Exists(inRepo)) return inRepo;
        var hardcoded = @"D:\project\本地模型计算\phase4b_windows_manager_fork\repo\src\LocalLlmConsole.App\bin\Release\net10.0-windows\win-x64\LlamaCppWindowsManager.exe";
        if (File.Exists(hardcoded)) return hardcoded;
        return inRepo;
    }

    // Strictly isolated ports in 19250-19260 (NEVER production 19000/8082/18083/3001)
    private const int FacadePort = 19250;
    private const int CyberPort = 19251;
    private const int QwenPort = 19252;
    private const int InklingPort = 19253;
    private const int FailStartupPort = 19256;
    private const int InternalSwapPort = 19259;
    private const int ExternalMockPort = 19260;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public async Task Phase4B_Full25Point_Isolation_GuardIntegration_Cli_And_SecurityEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), $"phase4b_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var wmWorkspace = Path.Combine(workDir, "wm_workspace");
        Directory.CreateDirectory(wmWorkspace);
        var modelsDir = Path.Combine(wmWorkspace, "models");
        Directory.CreateDirectory(modelsDir);

        Process? internalSwap = null;
        Process? externalMock = null;
        GuardRuntimeHost? guardHost = null;
        GuardRuntimeHost? restartedGuard = null;
        Socket? blockerSocket = null;

        using var externalModeScope = ExternalExecutionPolicy.UseModeForTests(ExecutionBackendMode.ExternalLlamaSwap);

        try
        {
            // =================================================================
            // Item 4 & 5: P0 Native Process Supervisor & Built-In Gateway Hard Isolation
            // =================================================================
            var sup = new LlamaProcessSupervisor(
                new WslRuntimeStopService(new TrackedProcessRunner()),
                new NativeRuntimeStopService());
            var dummyRuntime = new RuntimeRecord("rt-a", "Runtime A", RuntimeMode.Native, RuntimeBackend.Cuda, FakeModelA, "{}", DateTimeOffset.UtcNow);
            var dummyModel = new ModelRecord("m-cyber", "Cyber-Tiel", Path.Combine(modelsDir, "cyber.gguf"), OwnershipKind.External, "{}", DateTimeOffset.UtcNow);
            var dummySettings = AppSettings.CreateDefault(wmWorkspace);

            var startBlocked = await Assert.ThrowsAsync<NativeProcessControlBlockedException>(
                () => sup.StartAsync(dummyRuntime, dummyModel, dummySettings, Path.Combine(wmWorkspace, "logs")));
            var attachBlocked = Assert.Throws<NativeProcessControlBlockedException>(
                () => sup.AttachExisting(dummyRuntime, dummyModel.Id, dummySettings, ""));

            var sessionMgr = new LoadedModelSessionManager(() => sup);
            var sessionStartBlocked = await Assert.ThrowsAsync<NativeProcessControlBlockedException>(
                () => sessionMgr.StartAsync(dummyRuntime, dummyModel, dummySettings, Path.Combine(wmWorkspace, "logs")));

            // Native Gateway MUST be NullModelGatewayHost (IsListening == false)
            var gwFactory = new ModelGatewayHostFactoryService();
            var gwHost = gwFactory.CreateGatewayHost(
                ModelGatewayOptions.FromSettings(dummySettings with { AutoLoadGatewayEnabled = true, AutoLoadGatewayPort = 19257 }),
                gwFactory.CreateRuntimeController(new ModelGatewayRuntimeControllerActions(
                    _ => Task.FromResult<IReadOnlyList<ModelGatewayModelRoute>>([]),
                    _ => Task.FromResult<IReadOnlyList<LoadedModelSessionSnapshot>>([]),
                    (_, _, _) => throw new InvalidOperationException())));
            Assert.IsType<NullModelGatewayHost>(gwHost);
            await gwHost.StartAsync(ct);
            Assert.False(gwHost.IsListening);

            // Recovery MUST no-op in ExternalLlamaSwap mode
            var recoverySvc = new RuntimeSessionRecoveryApplicationService(
                sessionMgr,
                new RuntimeSessionPersistenceService(new ActiveRuntimeSessionStore(Path.Combine(wmWorkspace, "active.json")), sessionMgr),
                new RuntimeSessionRecoveryService(sessionMgr));
            var recResult = await recoverySvc.RecoverAsync(new RuntimeSessionRecoveryApplicationActions(
                () => Task.FromResult<IReadOnlyList<ModelRecord>>([]),
                () => Task.FromResult<IReadOnlyList<RuntimeRecord>>([]),
                (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
                (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult(false),
                (_, _) => { },
                _ => { },
                _ => { },
                _ => { },
                () => { },
                () => Task.CompletedTask,
                () => Task.CompletedTask), ct);
            Assert.False(recResult.Attempted);

            // =================================================================
            // Item 3: Guard Offline Auto Read-Only Mode
            // =================================================================
            var pipeName = $"LocalLLMManager.Guard.P4B.{Guid.NewGuid():N}";
            var bridge = new GuardControlBridgeService(new GuardBackendOptions(
                pipeName,
                $"http://127.0.0.1:{FacadePort}/v1",
                "local-coder",
                wmWorkspace,
                null));

            var offlineSnap = await bridge.PollStatusAsync(ct);
            Assert.False(offlineSnap.GuardOnline);
            Assert.True(offlineSnap.DisplayState is UnifiedDisplayState.GUARD_OFFLINE or UnifiedDisplayState.READ_ONLY_OBSERVATION);

            // Verify 10-state distinction: PROFILE_ACTIVE MUST NOT be reported as MODEL_READY / MODEL_INFERENCE_VERIFIED
            var profileOnlyState = GuardControlBridgeService.ResolveDisplayState(
                new GuardStatusSnapshot
                {
                    GatewayHealthy = true,
                    OperationalState = GuardOperationalState.ALLOW_SWITCH,
                    ActiveProfile = "cyber-tiel",
                    RunningManagedModels = []
                },
                inferenceVerified: false,
                lastError: null);
            Assert.Equal(UnifiedDisplayState.PROFILE_ACTIVE, profileOnlyState);

            // =================================================================
            // Item 6, 7, 8, 9: SQLite Asset Scan, Multi-Runtime Registration + SHA-256, Launch Profile Edit & One-Way Sync
            // =================================================================
            var dbPath = Path.Combine(wmWorkspace, "state.db");
            var store = new StateStore(dbPath);
            await store.InitializeAsync();

            // Create minimal valid GGUF header mock files (magic "GGUF" v3) for scanning (ZERO real large GGUF copy!)
            _ = CreateMinimalMockGguf(Path.Combine(modelsDir, "Cyber-Tiel-Coder-35B-A3B-Q3_K_XL.gguf"));
            _ = CreateMinimalMockGguf(Path.Combine(modelsDir, "Qwen3.6-35B-A3B-Q4_K_XL.gguf"));
            _ = CreateMinimalMockGguf(Path.Combine(modelsDir, "Inkling-2-Mini-Q4_K_M.gguf"));

            var catalog = new ModelCatalogService(store);
            _ = await catalog.ScanDetailedAsync(modelsDir);
            var registeredModels = await store.ListModelsAsync();
            Assert.True(registeredModels.Count >= 3, $"Expected >=3 scanned mock GGUF models, found {registeredModels.Count}");

            // Register 2 distinct Runtimes (Runtime A & Runtime B) + compute SHA-256
            var rtA = new RuntimeRecord("runtime-a", "llama.cpp b12328 (CUDA 12.4)", RuntimeMode.Native, RuntimeBackend.Cuda, FakeModelA, "{}", DateTimeOffset.UtcNow);
            var rtB = new RuntimeRecord("runtime-b", "llama.cpp Round151-CandB (CUDA 12.4)", RuntimeMode.Native, RuntimeBackend.Cuda, FakeModelB, "{}", DateTimeOffset.UtcNow);
            await store.UpsertRuntimeAsync(rtA);
            await store.UpsertRuntimeAsync(rtB);
            var shaA = GuardProfileSyncService.ComputeFileSha256(FakeModelA);
            var shaB = GuardProfileSyncService.ComputeFileSha256(FakeModelB);
            Assert.Equal(64, shaA.Length);
            Assert.Equal(64, shaB.Length);

            // Launch Profile edit (candidate-only, with MoE --n-cpu-moe 23, NEVER launches llama-server!)
            var profileSvc = new ModelLaunchProfileService(store, sessionMgr);
            var savedProfiles = new List<NamedModelLaunchProfile>();
            int nextPort = CyberPort;
            foreach (var m in registeredModels.Take(3))
            {
                var def = await profileSvc.EnsureDefaultAsync(m, dummySettings);
                var edited = def with
                {
                    Settings = def.Settings with
                    {
                        RuntimeId = m.Name.Contains("Inkling", StringComparison.OrdinalIgnoreCase) ? rtB.Id : rtA.Id,
                        ContextSize = m.Name.Contains("Cyber", StringComparison.OrdinalIgnoreCase) ? 131072 : 65536,
                        GpuLayers = 99,
                        Port = nextPort++,
                        CustomParameters = m.Name.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ? "--n-cpu-moe 23" : ""
                    },
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await profileSvc.SaveNamedAsync(edited);
                savedProfiles.Add(edited);
            }

            // One-way SQLite -> Guard YAML sync export
            var syncArtifact = GuardProfileSyncService.ExportCandidateSyncArtifact(
                registeredModels,
                await store.ListRuntimesAsync(),
                savedProfiles,
                dummySettings,
                "local-coder");
            Assert.Contains("local-coder:", syncArtifact.GeneratedYaml);
            Assert.Contains("gpu_exclusive:", syncArtifact.GeneratedYaml);
            Assert.Equal(3, syncArtifact.ProfileSpecs.Count);

            // =================================================================
            // Start Isolated Guard + Internal Protected llama-swap v275 (19250 -> 19259)
            // =================================================================
            if (!File.Exists(LlamaSwapExe))
            {
                Assert.Skip("Phase 4B end-to-end integration test requires vendor llama-swap.exe binary (missing on clean cloud runner).");
                return;
            }
            var internalKey = "guard-internal-p4b0123456789abcdef0123456789abcdef";
            var agentKey = "agent-inf-p4b9876543210fedcba9876543210";
            var guardStateDir = Path.Combine(workDir, "guard_state");
            var swapYaml = CreateIsolatedSwapYaml(Path.Combine(workDir, "swap-19259.yaml"), internalKey);
            internalSwap = await StartLlamaSwapAsync(InternalSwapPort, swapYaml, ct);

            var guardProfiles = new Dictionary<string, GuardProfileSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["cyber-tiel"] = new() { ProfileName = "cyber-tiel", ModelId = "cyber-tiel-mock", UpstreamPort = CyberPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, IsIsolatedMockProfile = true },
                ["qwen36"] = new() { ProfileName = "qwen36", ModelId = "qwen-mock", UpstreamPort = QwenPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, IsIsolatedMockProfile = true },
                ["inkling2"] = new() { ProfileName = "inkling2", ModelId = "inkling2-mock", UpstreamPort = InklingPort, RuntimeExecutablePath = FakeModelB, ExpectedRuntimeSha256 = shaB, IsIsolatedMockProfile = true },
                ["fail-startup"] = new() { ProfileName = "fail-startup", ModelId = "fail-startup-mock", UpstreamPort = FailStartupPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, IsIsolatedMockProfile = true }
            };

            GuardHostConfig MakeGuardConfig() => new()
            {
                StateDirectory = guardStateDir,
                PipeName = pipeName,
                AgentFacadeListenPort = FacadePort,
                InternalLlamaSwapBaseUrl = $"http://127.0.0.1:{InternalSwapPort}",
                RequireAgentInferenceApiKey = true,
                OverrideCredentials = new GuardCredentialSet(internalKey, agentKey, DateTimeOffset.UtcNow),
                Profiles = guardProfiles,
                DiscoveryTargetImageNames = ["fake-model.exe"],
                DiscoveryPortRangeMin = 19250,
                DiscoveryPortRangeMax = 19260,
                ManagedGatewayPidProvider = () => internalSwap.Id
            };

            guardHost = GuardHostFactory.Build(MakeGuardConfig());
            await guardHost.StartAsync(ct);

            // Item 2, 10, 11: Guard Connected + Live Status Refresh + GPU/RAM/Commit Telemetry
            var onlineSnap = await bridge.PollStatusAsync(ct);
            Assert.True(onlineSnap.GuardOnline);
            Assert.NotNull(onlineSnap.RawGuardStatus?.Telemetry);
            Assert.True(onlineSnap.RawGuardStatus!.Telemetry!.RamAvailable);
            Assert.True(onlineSnap.RawGuardStatus.Telemetry.CommitAvailable);

            // Item 12 & 25: Switch via Guard (UI role) + Fixed API (19250/v1) & local-coder unchanged!
            var swCyber = await bridge.SwitchProfileAsync("cyber-tiel", GuardCallerRole.UiClient, ct: ct);
            Assert.True(swCyber.Succeeded, swCyber.Message);
            var swQwen = await bridge.SwitchProfileAsync("qwen36", GuardCallerRole.UiClient, ct: ct);
            Assert.True(swQwen.Succeeded, swQwen.Message);

            // Item 19: Real llwmctl.exe CLI sharing the EXACT same Guard ACL Named Pipe!
            var cliSwitchOut = RunProcess(LlwmctlExe, $"switch inkling2 --guard-pipe {pipeName}");
            Assert.Equal(0, cliSwitchOut.ExitCode);
            var cliGwHealth = RunProcess(LlwmctlExe, $"gateway health --guard-pipe {pipeName}");
            Assert.Equal(0, cliGwHealth.ExitCode);
            var cliGwStatus = RunProcess(LlwmctlExe, $"gateway status --guard-pipe {pipeName}");
            Assert.Equal(0, cliGwStatus.ExitCode);
            var cliLogsAudit = RunProcess(LlwmctlExe, $"logs audit --guard-pipe {pipeName}");
            Assert.Equal(0, cliLogsAudit.ExitCode);
            var cliRollback = RunProcess(LlwmctlExe, $"rollback --guard-pipe {pipeName}");
            Assert.Equal(0, cliRollback.ExitCode);

            // Verify CLI contract paths for models inspect / runtimes inspect / models list / profiles list
            var rModelsInspect = ControlCliRequestFactory.BuildForTests("models", "inspect", "cyber-tiel");
            var rRuntimesInspect = ControlCliRequestFactory.BuildForTests("runtimes", "inspect", "rt-a");
            Assert.Equal("GET", rModelsInspect.Method);
            Assert.Equal("/api/v1/models/cyber-tiel", rModelsInspect.Path);
            Assert.Equal("GET", rRuntimesInspect.Method);
            Assert.Equal("/api/v1/runtimes", rRuntimesInspect.Path);

            // Item 20: Security — Agent inference key gets 200 on /v1/chat/completions, 403 on PUT /api/profiles/active, ZERO secret leak in CLI output!
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var agentChatResp = await SendHttpAsync(http, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentKey,
                "{\"model\":\"local-coder\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"fixed api check\"}]}", ct);
            var agentAdminBlocked = await SendHttpAsync(http, HttpMethod.Put, $"http://127.0.0.1:{FacadePort}/api/profiles/active", agentKey, "{\"name\":\"qwen36\"}", ct);
            Assert.Equal(200, agentChatResp.StatusCode);
            Assert.Equal(403, agentAdminBlocked.StatusCode);
            Assert.DoesNotContain(internalKey, cliGwStatus.Stdout);
            Assert.DoesNotContain(internalKey, cliLogsAudit.Stdout);

            // Item 13, 14, 15: Long SSE Drain + FIFO + SSE Loading Progress through Guard Facade (19250 -> 19259)
            await bridge.SwitchProfileAsync("cyber-tiel", GuardCallerRole.UiClient, ct: ct);
            var r1First = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r1Task = StreamSseAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentKey, "R1", ct, () => r1First.TrySetResult(true));
            Assert.True(await Task.WhenAny(r1First.Task, Task.Delay(10000, ct)) == r1First.Task);
            await Task.Delay(400, ct);

            using (var internalAdmin = new LocalLLMManager.Guard.Service.Control.LlamaSwapAdminClient($"http://127.0.0.1:{InternalSwapPort}", internalKey))
            {
                var (pinOk, _, _) = await internalAdmin.SetActiveProfileAsync("qwen36", ct);
                Assert.True(pinOk);
            }

            var r2Task = StreamSseAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentKey, "R2", ct);
            await Task.Delay(100, ct);
            var r3Task = StreamSseAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentKey, "R3", ct);
            await Task.Delay(100, ct);
            var r4Task = StreamSseAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentKey, "R4", ct);

            var sseAll = await Task.WhenAll(r1Task, r2Task, r3Task, r4Task);
            Assert.All(sseAll, r => Assert.Equal(200, r.StatusCode));
            Assert.All(sseAll, r => Assert.True(r.SawDone));
            Assert.Equal(80, sseAll[0].WordCount);
            Assert.Contains("Queue position: #1", sseAll[1].Banner);
            Assert.Contains("Queue position: #2", sseAll[2].Banner);
            Assert.Contains("Queue position: #3", sseAll[3].Banner);

            // Item 18: External Codex / Unmanaged Model Protection on isolated port 19260 (zero interference!)
            await bridge.SwitchProfileAsync("cyber-tiel", GuardCallerRole.UiClient, ct: ct);
            externalMock = Process.Start(new ProcessStartInfo
            {
                FileName = FakeModelB,
                Arguments = $"-listen 127.0.0.1:{ExternalMockPort} -tokens 16 -tps 100 -load 100ms",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            await WaitHealth200Async($"http://127.0.0.1:{ExternalMockPort}/health", ct);

            var blockedByExt = await bridge.SwitchProfileAsync("qwen36", GuardCallerRole.UiClient, ct: ct);
            Assert.False(blockedByExt.Succeeded);
            Assert.Equal("BLOCKED_BY_EXTERNAL_GPU_OWNER", blockedByExt.Code);
            Assert.Equal(GuardOperationalState.READ_ONLY_OBSERVATION, blockedByExt.OperationalState);
            Assert.False(externalMock.HasExited);
            var extHealthAfterBlock = await SendHttpAsync(http, HttpMethod.Get, $"http://127.0.0.1:{ExternalMockPort}/health", null, null, ct);
            Assert.Equal(200, extHealthAfterBlock.StatusCode);

            StopProc(externalMock);
            externalMock = null;
            await Task.Delay(300, ct);

            // Item 1: Real Standalone WPF Desktop Launch (LlamaCppWindowsManager.exe) + Live Screenshots against Isolated Guard!
            var screenshotsDir = Path.Combine(ForkOutDir, "wpf_screenshots");
            var wpfProc = RunProcess(
                WmAppExe,
                $"--isolated-instance --workspace \"{wmWorkspace}\" --guard-pipe {pipeName} --gateway-url http://127.0.0.1:{FacadePort}/v1 --capture-screenshots \"{screenshotsDir}\"",
                timeoutMs: 35000);
            Assert.Equal(0, wpfProc.ExitCode);
            Assert.True(File.Exists(Path.Combine(screenshotsDir, "phase4b_01_overview_guard_center.png")));
            Assert.True(File.Exists(Path.Combine(screenshotsDir, "phase4b_02_models_and_launch_profile.png")));
            Assert.True(File.Exists(Path.Combine(screenshotsDir, "phase4b_03_runtimes_registry.png")));

            // Item 16 & 17: Startup Failure Auto-Rollback + Double Fault FAULT_LOCKED
            var rbFail = await bridge.SwitchProfileAsync("fail-startup", GuardCallerRole.UiClient, ct: ct);
            Assert.Equal(SwitchFailureClass.MODEL_STARTUP_FAILED, rbFail.FailureClass);
            Assert.Equal(RollbackOutcome.ROLLBACK_SUCCEEDED, rbFail.RollbackOutcome);
            Assert.Equal("cyber-tiel", rbFail.ActiveProfile);

            guardHost.Coordinator.BeforeRollbackRestoreHookAsync = _ =>
            {
                blockerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                blockerSocket.Bind(new IPEndPoint(IPAddress.Loopback, CyberPort));
                blockerSocket.Listen(1);
                return Task.CompletedTask;
            };
            var doubleFault = await bridge.SwitchProfileAsync("fail-startup", GuardCallerRole.UiClient, ct: ct);
            guardHost.Coordinator.BeforeRollbackRestoreHookAsync = null;
            Assert.Equal(RollbackOutcome.ROLLBACK_FAILED, doubleFault.RollbackOutcome);
            Assert.Equal(GuardOperationalState.FAULT_LOCKED, doubleFault.OperationalState);
            blockerSocket?.Dispose();
            blockerSocket = null;

            // Item 21 & 22: Guard Restart Recovery + App Bridge Restart Recovery!
            await guardHost.DisposeAsync();
            guardHost = null;
            restartedGuard = GuardHostFactory.Build(MakeGuardConfig());
            await restartedGuard.StartAsync(ct);

            var freshAppBridge = new GuardControlBridgeService(new GuardBackendOptions(pipeName, $"http://127.0.0.1:{FacadePort}/v1", "local-coder", wmWorkspace, null));
            var recoveredSnap = await freshAppBridge.PollStatusAsync(ct);
            Assert.True(recoveredSnap.GuardOnline);
            Assert.Equal(UnifiedDisplayState.FAULT_LOCKED, recoveredSnap.DisplayState);
            Assert.Equal("cyber-tiel", recoveredSnap.LastKnownGoodProfile);

            var unlocked = await freshAppBridge.UnlockFaultAsync("Phase 4B restart recovery verification", ct: ct);
            Assert.True(unlocked.Succeeded);
            var postRecoverySwitch = await freshAppBridge.SwitchProfileAsync("cyber-tiel", GuardCallerRole.UiClient, ct: ct);
            Assert.True(postRecoverySwitch.Succeeded);

            // Item 23 & 24: Historical Session Log Retention, Search, Filters (Error & CUDA/OOM), Redaction & Export
            var logSvc = new GuardSessionLogService(Path.Combine(wmWorkspace, "logs", "guard-sessions"));
            logSvc.Append("sess-001", "cyber-tiel-mock", "INFO", $"Loaded model with Authorization: Bearer {internalKey}");
            logSvc.Append("sess-001", "qwen-mock", "ERROR", $"CUDA out of memory while loading with {agentKey}");
            logSvc.Append("sess-002", "inkling2-mock", "INFO", "Inference verified OK");

            var errFiltered = logSvc.Query(new GuardLogFilter(ErrorsOnly: true));
            var cudaFiltered = logSvc.Query(new GuardLogFilter(CudaOrOomOnly: true));
            var searchFiltered = logSvc.Query(new GuardLogFilter(SearchText: "verified"));
            var histSessions = logSvc.ListHistoricalSessions();
            var exportedFile = logSvc.ExportFilteredLogs(Path.Combine(workDir, "exported-redacted.log"));
            var exportedContent = File.ReadAllText(exportedFile, Encoding.UTF8);

            Assert.Single(errFiltered);
            Assert.Single(cudaFiltered);
            Assert.Single(searchFiltered);
            Assert.True(histSessions.Count >= 2);
            Assert.DoesNotContain(internalKey, exportedContent);
            Assert.DoesNotContain(agentKey, exportedContent);
            Assert.Contains("***REDACTED***", exportedContent);
            Assert.False(GuardSessionLogService.IsIsolatedFromExternalLogs("codex-session.log"));
            Assert.False(GuardSessionLogService.IsIsolatedFromExternalLogs("hermes-agent.log"));

            // Save complete raw JSON security & 25-point evidence
            var evidencePath = Path.Combine(ForkOutDir, "phase4b_security_evidence.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                phase = "Phase 4B Controlled Fork + Guard Integration (25-Point Verification)",
                executedAtUtc = DateTimeOffset.UtcNow,
                upstreamBaseline = new { repo = "alekk89/llama-cpp-windows-manager", tag = "v3.0.0", commit = "3b0c87939c3146c0e6d5a594fe88f6809a3ac8c2", license = "MIT" },
                p0NativeIsolation = new
                {
                    supervisorStartBlocked = startBlocked.BlockedOperation,
                    supervisorAttachBlocked = attachBlocked.BlockedOperation,
                    sessionManagerStartBlocked = sessionStartBlocked.BlockedOperation,
                    gatewayHostType = gwHost.GetType().Name,
                    gatewayIsListening = gwHost.IsListening,
                    recoveryAttempted = recResult.Attempted
                },
                assetAndProfilePipeline = new
                {
                    scannedModelsCount = registeredModels.Count,
                    runtimeASha256 = shaA,
                    runtimeBSha256 = shaB,
                    exportedProfilesCount = syncArtifact.ProfileSpecs.Count,
                    exportedYamlSha256 = syncArtifact.ConfigSha256
                },
                guardAuthAndCli = new
                {
                    guardOfflineState = offlineSnap.DisplayState.ToString(),
                    profileOnlyNotLoadedState = profileOnlyState.ToString(),
                    agentInferenceStatus = agentChatResp.StatusCode,
                    agentAdminPutProfileBlockedStatus = agentAdminBlocked.StatusCode,
                    cliSwitchExitCode = cliSwitchOut.ExitCode,
                    cliGatewayHealthExitCode = cliGwHealth.ExitCode,
                    cliGatewayStatusExitCode = cliGwStatus.ExitCode,
                    cliRollbackExitCode = cliRollback.ExitCode,
                    cliLogsAuditExitCode = cliLogsAudit.ExitCode,
                    secretsLeakedInCliOrExports = false
                },
                sseDrainAndFifo = new
                {
                    r1WordCount = sseAll[0].WordCount,
                    r2HasQueuePos1 = sseAll[1].Banner.Contains("Queue position: #1"),
                    r3HasQueuePos2 = sseAll[2].Banner.Contains("Queue position: #2"),
                    r4HasQueuePos3 = sseAll[3].Banner.Contains("Queue position: #3"),
                    zeroHttp429 = sseAll.All(x => x.StatusCode == 200)
                },
                externalProcessProtection = new
                {
                    mockExternalPort = ExternalMockPort,
                    blockedCode = blockedByExt.Code,
                    blockedState = blockedByExt.OperationalState.ToString(),
                    externalHealthAfterBlock = extHealthAfterBlock.StatusCode
                },
                rollbackAndRestartRecovery = new
                {
                    startupFailRollbackOutcome = rbFail.RollbackOutcome.ToString(),
                    doubleFaultState = doubleFault.OperationalState.ToString(),
                    recoveredStateAfterGuardAndAppRestart = recoveredSnap.DisplayState.ToString(),
                    postUnlockSwitchSucceeded = postRecoverySwitch.Succeeded
                },
                wpfRealDesktopLaunch = new
                {
                    exitCode = wpfProc.ExitCode,
                    screenshots = new[]
                    {
                        "wpf_screenshots/phase4b_01_overview_guard_center.png",
                        "wpf_screenshots/phase4b_02_models_and_launch_profile.png",
                        "wpf_screenshots/phase4b_03_runtimes_registry.png"
                    }
                },
                logsAndSessionRetention = new
                {
                    historicalSessionsCount = histSessions.Count,
                    errorFilterCount = errFiltered.Count,
                    cudaOomFilterCount = cudaFiltered.Count,
                    searchFilterCount = searchFiltered.Count,
                    redactedExportVerified = true
                },
                all25PointsPassed = true
            }, PrettyJson), Encoding.UTF8);
        }
        finally
        {
            blockerSocket?.Dispose();
            StopProc(externalMock);
            if (guardHost is not null) await guardHost.DisposeAsync();
            if (restartedGuard is not null) await restartedGuard.DisposeAsync();
            StopProc(internalSwap);
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static string CreateMinimalMockGguf(string path)
    {
        // Minimal valid GGUF v3 header with general.architecture + context_length metadata so ModelCatalogService classifies it as a primary model
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);
        bw.Write("GGUF"u8);
        bw.Write((uint)3); // version
        bw.Write((ulong)0); // tensor_count
        bw.Write((ulong)2); // metadata_kv_count
        WriteGgufString(bw, "general.architecture");
        bw.Write((uint)8); // GGUF_TYPE_STRING
        WriteGgufString(bw, "qwen3");
        WriteGgufString(bw, "qwen3.context_length");
        bw.Write((uint)4); // GGUF_TYPE_UINT32
        bw.Write((uint)131072);
        return path;
    }

    private static void WriteGgufString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write((ulong)bytes.Length);
        bw.Write(bytes);
    }

    private static string CreateIsolatedSwapYaml(string path, string apiKey)
    {
        var fmA = FakeModelA.Replace("\\", "/");
        var fmB = FakeModelB.Replace("\\", "/");
        var yaml = $"""
healthCheckTimeout: 4
logLevel: debug
sendLoadingState: true
globalConcurrencyLimit: 0
apiKeys:
  - "{apiKey}"
hooks:
  on_startup:
    profile: cyber-tiel
profiles:
  cyber-tiel:
    description: "Cyber-Tiel Mock"
    pins:
      local-coder: cyber-tiel-mock
  qwen36:
    description: "Qwen3.6 Mock"
    pins:
      local-coder: qwen-mock
  inkling2:
    description: "Inkling-2 Mock"
    pins:
      local-coder: inkling2-mock
  fail-startup:
    description: "Intentional Startup Fail Mock"
    pins:
      local-coder: fail-startup-mock
models:
  cyber-tiel-mock:
    proxy: "http://127.0.0.1:{CyberPort}"
    cmd: '"{fmA}" -listen 127.0.0.1:{CyberPort} -tokens 80 -tps 30 -load 200ms'
    checkEndpoint: "/health"
  qwen-mock:
    proxy: "http://127.0.0.1:{QwenPort}"
    cmd: '"{fmA}" -listen 127.0.0.1:{QwenPort} -tokens 28 -tps 110 -load 250ms'
    checkEndpoint: "/health"
  inkling2-mock:
    proxy: "http://127.0.0.1:{InklingPort}"
    cmd: '"{fmB}" -listen 127.0.0.1:{InklingPort} -tokens 22 -tps 120 -load 200ms'
    checkEndpoint: "/health"
  fail-startup-mock:
    proxy: "http://127.0.0.1:{FailStartupPort}"
    cmd: '"{fmA}" --intentional-invalid-flag-phase4b'
    checkEndpoint: "/health"
groups:
  gpu_exclusive:
    swap: true
    exclusive: true
    members:
      - cyber-tiel-mock
      - qwen-mock
      - inkling2-mock
      - fail-startup-mock
""";
        File.WriteAllText(path, yaml, Encoding.UTF8);
        return path;
    }

    private static async Task<Process> StartLlamaSwapAsync(int port, string yamlPath, CancellationToken ct)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = LlamaSwapExe,
                Arguments = $"-listen 127.0.0.1:{port} -config \"{yamlPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await WaitHealth200Async($"http://127.0.0.1:{port}/health", ct);
        return proc;
    }

    private static async Task WaitHealth200Async(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var r = await http.GetAsync(url, ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(150, ct);
        }
        throw new TimeoutException($"Endpoint did not return 200 OK: {url}");
    }

    private static void StopProc(Process? proc)
    {
        if (proc is null) return;
        try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); } }
        catch { }
        finally { proc.Dispose(); }
    }

    private sealed record ProcRun(int ExitCode, string Stdout, string Stderr);

    private static ProcRun RunProcess(string exe, string args, int timeoutMs = 20000)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Process timed out: {exe} {args}");
        }
        return new ProcRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private sealed record HttpRes(int StatusCode, string Body);

    private static async Task<HttpRes> SendHttpAsync(HttpClient http, HttpMethod method, string url, string? bearer, string? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        return new HttpRes((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private sealed record SseRes(string Id, int StatusCode, bool SawDone, int WordCount, string Banner);

    private static async Task<SseRes> StreamSseAsync(string url, string bearer, string id, CancellationToken ct, Action? onFirstContent = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model = "local-coder", stream = true, messages = new[] { new { role = "user", content = $"SSE {id}" } } }),
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var banner = new StringBuilder();
        var content = new StringBuilder();
        bool sawDone = false;
        bool signaled = false;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
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
                        banner.Append(rc.GetString());
                    if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var t = contentEl.GetString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (!signaled) { signaled = true; onFirstContent?.Invoke(); }
                            content.Append(t);
                        }
                    }
                }
            }
            catch { }
        }
        var words = content.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return new SseRes(id, (int)resp.StatusCode, sawDone, words, banner.ToString());
    }
}
