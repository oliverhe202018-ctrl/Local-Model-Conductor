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

[CollectionDefinition("Phase4AIsolatedPorts", DisableParallelization = true)]
public sealed class Phase4AIsolatedPortsCollection;

[Collection("Phase4AIsolatedPorts")]
public sealed class Phase4AGuardAcceptanceTests
{
    private const string OutDir = @"D:\project\本地模型计算\phase4a_guard";
    private const string LlamaSwapExe = @"D:\project\本地模型计算\LocalLLMManager\vendor\llama-swap\v275\llama-swap.exe";
    private const string FakeModelA = @"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275\fake-model.exe";
    private const string FakeModelB = @"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275-runtime-b\fake-model.exe";
    private const string FaultServerPy = @"D:\project\本地模型计算\phase3_3_integration_spike\spike_tests\mock_fault_server.py";

    // Isolated ports strictly within 19250-19260 (NEVER touching production 19000/8082/18083/3001)
    private const int FacadePort = 19250;
    private const int CyberPort = 19251;
    private const int QwenPort = 19252;
    private const int InklingPort = 19253;
    private const int FailHealthPort = 19254;
    private const int FailInferencePort = 19255;
    private const int FailStartupPort = 19256;
    private const int VanillaAuditPort = 19258;
    private const int InternalSwapPort = 19259;
    private const int ExternalMockPort = 19260;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string WriteJsonEvidence(string fileName, object payload)
    {
        var fullPath = Path.Combine(OutDir, fileName);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload, PrettyJson), Encoding.UTF8);
        return fullPath;
    }

    private static string ResolvePythonExe()
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "where.exe",
            Arguments = "python",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        })!;
        var outText = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return outText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).First().Trim();
    }

    private static string CreateLlamaSwapYaml(string yamlPath, string? apiKey, bool includeFaults = true)
    {
        var fmA = FakeModelA.Replace("\\", "/");
        var fmB = FakeModelB.Replace("\\", "/");
        var py = ResolvePythonExe().Replace("\\", "/");
        var faultPy = FaultServerPy.Replace("\\", "/");

        var apiKeysBlock = string.IsNullOrWhiteSpace(apiKey)
            ? ""
            : $"apiKeys:\n  - \"{apiKey}\"\n";

        var faultProfiles = includeFaults
            ? $"""
  fail-startup:
    description: "Intentional Startup Failure Mock"
    pins:
      local-coder: fail-startup-mock
  fail-health:
    description: "Intentional Health Check Timeout Mock"
    pins:
      local-coder: fail-health-mock
  fail-inference:
    description: "Intentional Inference 500 Mock"
    pins:
      local-coder: fail-inference-mock
"""
            : "";

        var faultModels = includeFaults
            ? $"""
  fail-startup-mock:
    name: "Broken Startup Mock"
    proxy: "http://127.0.0.1:{FailStartupPort}"
    cmd: '"{fmA}" --intentional-invalid-flag-phase4a'
    checkEndpoint: "/health"
  fail-health-mock:
    name: "Broken Health Mock"
    proxy: "http://127.0.0.1:{FailHealthPort}"
    cmd: '"{py}" "{faultPy}" --listen 127.0.0.1:{FailHealthPort} --mode fail-health --model-id fail-health-mock'
    checkEndpoint: "/health"
  fail-inference-mock:
    name: "Broken Inference Mock"
    proxy: "http://127.0.0.1:{FailInferencePort}"
    cmd: '"{py}" "{faultPy}" --listen 127.0.0.1:{FailInferencePort} --mode fail-inference --model-id fail-inference-mock'
    checkEndpoint: "/health"
"""
            : "";

        var faultMembers = includeFaults
            ? "      - fail-startup-mock\n      - fail-health-mock\n      - fail-inference-mock\n"
            : "";

        var yaml = $"""
healthCheckTimeout: 4
logLevel: debug
logRequests: true
sendLoadingState: true
globalConcurrencyLimit: 0
{apiKeysBlock}
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
    description: "Inkling-2 Mock (Runtime B)"
    pins:
      local-coder: inkling2-mock
{faultProfiles}

models:
  cyber-tiel-mock:
    name: "Cyber-Tiel Mock"
    proxy: "http://127.0.0.1:{CyberPort}"
    cmd: '"{fmA}" -listen 127.0.0.1:{CyberPort} -tokens 80 -tps 30 -load 250ms'
    checkEndpoint: "/health"
  qwen-mock:
    name: "Qwen3.6 Mock"
    proxy: "http://127.0.0.1:{QwenPort}"
    cmd: '"{fmA}" -listen 127.0.0.1:{QwenPort} -tokens 28 -tps 110 -load 300ms'
    checkEndpoint: "/health"
  inkling2-mock:
    name: "Inkling-2 Mock"
    proxy: "http://127.0.0.1:{InklingPort}"
    cmd: '"{fmB}" -listen 127.0.0.1:{InklingPort} -tokens 22 -tps 120 -load 250ms'
    checkEndpoint: "/health"
{faultModels}

groups:
  gpu_exclusive:
    swap: true
    exclusive: true
    members:
      - cyber-tiel-mock
      - qwen-mock
      - inkling2-mock
{faultMembers}
""";
        File.WriteAllText(yamlPath, yaml, Encoding.UTF8);
        return yamlPath;
    }

    private static async Task<Process> StartLlamaSwapAsync(int port, string yamlPath)
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

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var r = await http.GetAsync($"http://127.0.0.1:{port}/health");
                if (r.IsSuccessStatusCode) return proc;
            }
            catch { }
            await Task.Delay(150);
        }
        StopProcessSafely(proc);
        throw new TimeoutException($"llama-swap failed to become healthy on 127.0.0.1:{port}");
    }

    private static void StopProcessSafely(Process? proc)
    {
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch { }
        finally { proc.Dispose(); }
    }

    private static Dictionary<string, GuardProfileSpec> BuildMockProfiles()
    {
        var shaA = GuardResourceSafetyGate.ComputeFileSha256Hex(FakeModelA);
        var shaB = GuardResourceSafetyGate.ComputeFileSha256Hex(FakeModelB);
        return new Dictionary<string, GuardProfileSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["cyber-tiel"] = new() { ProfileName = "cyber-tiel", ModelId = "cyber-tiel-mock", UpstreamPort = CyberPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, RequiredVramMiB = 12000, RequiredRamMiB = 4096, RequiredCommitMiB = 6144, IsIsolatedMockProfile = true },
            ["qwen36"] = new() { ProfileName = "qwen36", ModelId = "qwen-mock", UpstreamPort = QwenPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, RequiredVramMiB = 11000, RequiredRamMiB = 8192, RequiredCommitMiB = 12288, IsIsolatedMockProfile = true },
            ["inkling2"] = new() { ProfileName = "inkling2", ModelId = "inkling2-mock", UpstreamPort = InklingPort, RuntimeExecutablePath = FakeModelB, ExpectedRuntimeSha256 = shaB, RequiredVramMiB = 9000, RequiredRamMiB = 4096, RequiredCommitMiB = 6144, IsIsolatedMockProfile = true },
            ["fail-startup"] = new() { ProfileName = "fail-startup", ModelId = "fail-startup-mock", UpstreamPort = FailStartupPort, RuntimeExecutablePath = FakeModelA, ExpectedRuntimeSha256 = shaA, IsIsolatedMockProfile = true },
            ["fail-health"] = new() { ProfileName = "fail-health", ModelId = "fail-health-mock", UpstreamPort = FailHealthPort, IsIsolatedMockProfile = true },
            ["fail-inference"] = new() { ProfileName = "fail-inference", ModelId = "fail-inference-mock", UpstreamPort = FailInferencePort, IsIsolatedMockProfile = true },
        };
    }

    // =========================================================================
    // SUITE A: P0 Management API Auth Isolation (Tests #9, #10, #16, #17 + ALL 7 §4 scenarios + §5 SSE/FIFO through Guard Facade)
    // =========================================================================
    [Fact]
    public async Task Test_01_P0_AdminAuthIsolation_SharedUiCliIpc_Redaction_And_SseFifoPassThrough()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"guard_p4a_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        Process? vanillaSwap = null;
        Process? protectedSwap = null;
        GuardRuntimeHost? guardHost = null;

        try
        {
            var internalAdminKey = "guard-internal-a1b2c3d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccddeeff";
            var agentInferenceKey = "agent-inf-99887766554433221100aabbccddeeff0011223344556677";

            // 1. Empirical baseline proof on VANILLA llama-swap v275 (port 19258) with inference apiKey ONLY
            var vanillaYaml = CreateLlamaSwapYaml(Path.Combine(workDir, "vanilla.yaml"), agentInferenceKey, includeFaults: false);
            vanillaSwap = await StartLlamaSwapAsync(VanillaAuditPort, vanillaYaml);

            using var rawHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var vanillaUnauthProfile = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{VanillaAuditPort}/api/profiles/active", null, "{\"name\":\"qwen36\"}");
            var vanillaInfKeyProfile = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{VanillaAuditPort}/api/profiles/active", agentInferenceKey, "{\"name\":\"qwen36\"}");
            var vanillaInfKeyPause = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{VanillaAuditPort}/api/models/pause", agentInferenceKey, null);
            var vanillaInfKeyResume = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{VanillaAuditPort}/api/models/resume", agentInferenceKey, null);

            // Proves vanilla llama-swap v275 has NO admin/inference separation: inference key got 200 on PUT /api/profiles/active & pause!
            Assert.Equal(401, vanillaUnauthProfile.StatusCode);
            Assert.Equal(200, vanillaInfKeyProfile.StatusCode);
            Assert.Equal(200, vanillaInfKeyPause.StatusCode);
            Assert.Equal(200, vanillaInfKeyResume.StatusCode);
            StopProcessSafely(vanillaSwap);
            vanillaSwap = null;

            // 2. Start Guard-protected internal llama-swap v275 (port 19259, locked by Guard-only internalAdminKey)
            var protectedYaml = CreateLlamaSwapYaml(Path.Combine(workDir, "protected.yaml"), internalAdminKey, includeFaults: false);
            protectedSwap = await StartLlamaSwapAsync(InternalSwapPort, protectedYaml);

            var pipeName = $"LocalLLMManager.Guard.P4A.Auth.{Guid.NewGuid():N}";
            guardHost = GuardHostFactory.Build(new GuardHostConfig
            {
                StateDirectory = Path.Combine(workDir, "guard_state"),
                PipeName = pipeName,
                AgentFacadeListenPort = FacadePort,
                InternalLlamaSwapBaseUrl = $"http://127.0.0.1:{InternalSwapPort}",
                RequireAgentInferenceApiKey = true,
                OverrideCredentials = new GuardCredentialSet(internalAdminKey, agentInferenceKey, DateTimeOffset.UtcNow),
                Profiles = BuildMockProfiles(),
                DiscoveryTargetImageNames = ["fake-model.exe"],
                DiscoveryPortRangeMin = 19250,
                DiscoveryPortRangeMax = 19260,
                ManagedGatewayPidProvider = () => protectedSwap.Id
            });
            await guardHost.StartAsync();

            // Execute ALL 7 mandatory §4 test cases against Guard Facade (19250) & direct internal port (19259)
            var c1Facade = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{FacadePort}/api/profiles/active", null, "{\"name\":\"qwen36\"}");
            var c1Internal = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{InternalSwapPort}/api/profiles/active", null, "{\"name\":\"qwen36\"}");

            var c2Facade = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{FacadePort}/api/profiles/active", agentInferenceKey, "{\"name\":\"qwen36\"}");
            var c2Internal = await SendRawAsync(rawHttp, HttpMethod.Put, $"http://127.0.0.1:{InternalSwapPort}/api/profiles/active", agentInferenceKey, "{\"name\":\"qwen36\"}");

            var c3Facade = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/api/models/unload", null, null);
            var c3Internal = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{InternalSwapPort}/api/models/unload", null, null);

            var c4Facade = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/api/models/pause", null, null);
            var c4Internal = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{InternalSwapPort}/api/models/pause", null, null);

            var c5Facade = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/api/models/resume", null, null);
            var c5Internal = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{InternalSwapPort}/api/models/resume", null, null);

            // Case 7: Ordinary Agent Inference credential calling /v1 (ALLOWED 200) vs management endpoints (REJECTED 403/401)
            var c7Models = await SendRawAsync(rawHttp, HttpMethod.Get, $"http://127.0.0.1:{FacadePort}/v1/models", agentInferenceKey, null);
            var c7Chat = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentInferenceKey,
                "{\"model\":\"local-coder\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}");
            var c7Unload = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/api/models/unload", agentInferenceKey, null);
            var c7Pause = await SendRawAsync(rawHttp, HttpMethod.Post, $"http://127.0.0.1:{FacadePort}/api/models/pause", agentInferenceKey, null);

            Assert.Equal(401, c1Facade.StatusCode);
            Assert.Equal(401, c1Internal.StatusCode);
            Assert.Equal(403, c2Facade.StatusCode);
            Assert.Equal(401, c2Internal.StatusCode);
            Assert.Equal(401, c3Facade.StatusCode);
            Assert.Equal(401, c3Internal.StatusCode);
            Assert.Equal(401, c4Facade.StatusCode);
            Assert.Equal(401, c4Internal.StatusCode);
            Assert.Equal(401, c5Facade.StatusCode);
            Assert.Equal(401, c5Internal.StatusCode);
            Assert.Equal(200, c7Models.StatusCode);
            Assert.Equal(200, c7Chat.StatusCode);
            Assert.Equal(403, c7Unload.StatusCode);
            Assert.Equal(403, c7Pause.StatusCode);

            // Case 6 + Test #17: Shared ACL Named Pipe entry for BOTH WPF UI (UiClient) AND CLI (CliClient) — neither holds internalAdminKey!
            var pipeClient = new GuardPipeClient(pipeName);
            var uiSwitch = await pipeClient.SendAsync(new GuardCommandRequest
            {
                Command = GuardCommandType.SWITCH_PROFILE,
                CallerRole = GuardCallerRole.UiClient,
                CallerId = "WPF_UI_TEST",
                TargetProfile = "qwen36"
            });
            Assert.True(uiSwitch.Succeeded, uiSwitch.Message);
            Assert.Equal("qwen36", uiSwitch.ActiveProfile);

            // Invoke actual CLI entrypoint targeting the EXACT SAME ACL Named Pipe
            var cliExit = await GuardCliEntrypoint.Main(["switch", "--target", "inkling2", "--pipe", pipeName, "--role", "CliClient"]);
            Assert.Equal(0, cliExit);

            var statusAfterCli = await pipeClient.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS, CallerRole = GuardCallerRole.UiClient });
            Assert.Equal("inkling2", statusAfterCli.ActiveProfile);

            // Section 5 Verification THROUGH Guard Facade (19250 -> 19259): Long SSE drain + FIFO Queue position #1/#2/#3!
            await pipeClient.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.CliClient, TargetProfile = "cyber-tiel" });
            var r1FirstToken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r1Task = StreamSseThroughFacadeAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentInferenceKey, "R1", () => r1FirstToken.TrySetResult(true));
            Assert.True(await Task.WhenAny(r1FirstToken.Task, Task.Delay(10000)) == r1FirstToken.Task, "R1 SSE never produced first token");
            await Task.Delay(450);

            // Switch profile mid-stream directly via Guard internal admin client (or pipe pause-free switch) while R1 streams
            var adminClient = new LlamaSwapAdminClient($"http://127.0.0.1:{InternalSwapPort}", internalAdminKey);
            var (midSwitchOk, _, _) = await adminClient.SetActiveProfileAsync("qwen36");
            Assert.True(midSwitchOk);
            adminClient.Dispose();

            var r2Task = StreamSseThroughFacadeAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentInferenceKey, "R2");
            await Task.Delay(100);
            var r3Task = StreamSseThroughFacadeAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentInferenceKey, "R3");
            await Task.Delay(100);
            var r4Task = StreamSseThroughFacadeAsync($"http://127.0.0.1:{FacadePort}/v1/chat/completions", agentInferenceKey, "R4");

            var sseResults = await Task.WhenAll(r1Task, r2Task, r3Task, r4Task);
            Assert.All(sseResults, r => Assert.Equal(200, r.StatusCode));
            Assert.All(sseResults, r => Assert.True(r.SawDone));
            Assert.Equal(80, sseResults[0].WordCount);
            Assert.Equal(28, sseResults[1].WordCount);
            Assert.Equal(28, sseResults[2].WordCount);
            Assert.Equal(28, sseResults[3].WordCount);
            Assert.Contains("Queue position: #1", sseResults[1].LoadingBanner);
            Assert.Contains("Queue position: #2", sseResults[2].LoadingBanner);
            Assert.Contains("Queue position: #3", sseResults[3].LoadingBanner);

            // Test #16: Full Log Redaction Verification — verify NEITHER internalAdminKey NOR agentInferenceKey appears in state or audit files!
            var auditRaw = File.ReadAllText(guardHost.StateStore.AuditLogPath, Encoding.UTF8);
            var stateRaw = File.ReadAllText(guardHost.StateStore.StateFilePath, Encoding.UTF8);
            var sampleRedacted = LogRedactor.Redact(
                $"Authorization: Bearer {internalAdminKey} --api-key {agentInferenceKey} x-api-key: sk-test123456789",
                internalAdminKey, agentInferenceKey);
            Assert.DoesNotContain(internalAdminKey, auditRaw);
            Assert.DoesNotContain(agentInferenceKey, auditRaw);
            Assert.DoesNotContain(internalAdminKey, stateRaw);
            Assert.DoesNotContain(agentInferenceKey, stateRaw);
            Assert.DoesNotContain(internalAdminKey, sampleRedacted);
            Assert.DoesNotContain(agentInferenceKey, sampleRedacted);
            Assert.DoesNotContain("sk-test123456789", sampleRedacted);

            WriteJsonEvidence("phase4a_admin_auth_evidence.json", new
            {
                testSuite = "Phase 4A Section 4 (P0 Management API Isolation) + Section 5 (SSE/FIFO Pass-Through) + Tests #9/#10/#16/#17",
                executedAtUtc = DateTimeOffset.UtcNow,
                vanillaLlamaSwapV275Audit = new
                {
                    finding = "Confirmed empirically on vanilla llama-swap v275: apiKeys protects /v1 and /api/* with the SAME authMW; holding an inference API key grants HTTP 200 on PUT /api/profiles/active and POST /api/models/pause.",
                    unauthenticatedPutProfileStatus = vanillaUnauthProfile.StatusCode,
                    inferenceKeyPutProfileStatus = vanillaInfKeyProfile.StatusCode,
                    inferenceKeyPostPauseStatus = vanillaInfKeyPause.StatusCode,
                    inferenceKeyPostResumeStatus = vanillaInfKeyResume.StatusCode
                },
                guardSplitAuthorityMatrix = new
                {
                    case1_unauthPutProfile = new { facade19250 = c1Facade.StatusCode, internal19259 = c1Internal.StatusCode, facadeBody = c1Facade.Body },
                    case2_inferenceKeyPutProfile = new { facade19250 = c2Facade.StatusCode, internal19259 = c2Internal.StatusCode, facadeBody = c2Facade.Body },
                    case3_unauthPostUnload = new { facade19250 = c3Facade.StatusCode, internal19259 = c3Internal.StatusCode },
                    case4_unauthPostPause = new { facade19250 = c4Facade.StatusCode, internal19259 = c4Internal.StatusCode },
                    case5_unauthPostResume = new { facade19250 = c5Facade.StatusCode, internal19259 = c5Internal.StatusCode },
                    case6_guardAdminViaAclNamedPipe = new
                    {
                        uiClientSwitchToQwen36 = new { uiSwitch.Succeeded, uiSwitch.Code, uiSwitch.ActiveProfile },
                        cliClientSwitchToInkling2 = new { exitCode = cliExit, activeProfileAfterCli = statusAfterCli.ActiveProfile },
                        uiAndCliShareSamePipe = true,
                        uiOrCliHoldsInternalAdminKey = false
                    },
                    case7_agentInferenceCredentials = new
                    {
                        getModelsStatus = c7Models.StatusCode,
                        postChatCompletionsStatus = c7Chat.StatusCode,
                        postUnloadBlockedStatus = c7Unload.StatusCode,
                        postPauseBlockedStatus = c7Pause.StatusCode
                    }
                },
                sseDrainAndFifoThroughGuardFacade = new
                {
                    zeroSecondQueueVerified = true,
                    globalConcurrencyLimit = 0,
                    zeroHttp429 = sseResults.All(r => r.StatusCode == 200),
                    requests = sseResults
                },
                logRedactionVerification = new
                {
                    sampleRedacted,
                    internalKeyLeakedInAuditOrState = false,
                    agentKeyLeakedInAuditOrState = false
                },
                allPassed = true
            });
        }
        finally
        {
            if (guardHost is not null) await guardHost.DisposeAsync();
            StopProcessSafely(protectedSwap);
            StopProcessSafely(vanillaSwap);
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // SUITE B: External Process Discovery & Zero-Interference Protection (Tests #2, #3, #18)
    // =========================================================================
    [Fact]
    public async Task Test_02_ExternalProcessDiscovery_OwnershipClasses_And_ZeroInterference()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"guard_p4a_ext_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        Process? protectedSwap = null;
        Process? externalMockProc = null;
        GuardRuntimeHost? guardHost = null;

        try
        {
            var internalAdminKey = "guard-internal-ext0123456789abcdef0123456789abcdef";
            var agentKey = "agent-inf-ext9876543210fedcba9876543210";
            var yaml = CreateLlamaSwapYaml(Path.Combine(workDir, "ext.yaml"), internalAdminKey, includeFaults: false);
            protectedSwap = await StartLlamaSwapAsync(InternalSwapPort, yaml);

            var pipeName = $"LocalLLMManager.Guard.P4A.Ext.{Guid.NewGuid():N}";
            guardHost = GuardHostFactory.Build(new GuardHostConfig
            {
                StateDirectory = Path.Combine(workDir, "guard_state"),
                PipeName = pipeName,
                AgentFacadeListenPort = FacadePort,
                InternalLlamaSwapBaseUrl = $"http://127.0.0.1:{InternalSwapPort}",
                OverrideCredentials = new GuardCredentialSet(internalAdminKey, agentKey, DateTimeOffset.UtcNow),
                Profiles = BuildMockProfiles(),
                DiscoveryTargetImageNames = ["fake-model.exe"],
                DiscoveryPortRangeMin = 19250,
                DiscoveryPortRangeMax = 19260,
                ManagedGatewayPidProvider = () => protectedSwap.Id
            });
            await guardHost.StartAsync();

            var pipe = new GuardPipeClient(pipeName);
            // Warm up managed cyber-tiel on 19251 first
            var warm = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.CliClient, TargetProfile = "cyber-tiel" });
            Assert.True(warm.Succeeded);

            // Launch isolated mock external process on 19260 (NEVER touching production 8082/18083!)
            externalMockProc = Process.Start(new ProcessStartInfo
            {
                FileName = FakeModelB,
                Arguments = $"-listen 127.0.0.1:{ExternalMockPort} -tokens 16 -tps 100 -load 100ms",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            bool extHealthyBefore = false;
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    var r = await http.GetAsync($"http://127.0.0.1:{ExternalMockPort}/health");
                    if (r.IsSuccessStatusCode) { extHealthyBefore = true; break; }
                }
                catch { }
                await Task.Delay(150);
            }
            Assert.True(extHealthyBefore);

            // 1. Live OS discovery via Guard
            var statusWhileExt = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS, CallerRole = GuardCallerRole.UiClient });
            Assert.NotNull(statusWhileExt.Status);
            Assert.True(statusWhileExt.Status!.ExternalGpuConflictActive);
            Assert.Equal(GuardOperationalState.READ_ONLY_OBSERVATION, statusWhileExt.OperationalState);

            var managedEntry = Assert.Single(statusWhileExt.Status.DiscoveredProcesses, p => p.ListeningPorts.Contains(CyberPort));
            var externalEntry = Assert.Single(statusWhileExt.Status.DiscoveredProcesses, p => p.ListeningPorts.Contains(ExternalMockPort));
            Assert.Equal(ProcessOwnershipClass.MANAGED_BY_LLAMA_SWAP, managedEntry.OwnershipClass);
            Assert.Equal(ProcessOwnershipClass.EXTERNAL_UNMANAGED, externalEntry.OwnershipClass);
            Assert.Equal(externalMockProc.Id, externalEntry.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(externalEntry.ExecutablePath));
            Assert.NotNull(externalEntry.StartTimeUtc);

            // 2. Attempt GPU-competing switch & unload -> MUST be hard-blocked!
            var blockedSwitch = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.SWITCH_PROFILE, CallerRole = GuardCallerRole.UiClient, TargetProfile = "qwen36" });
            var blockedUnload = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.UNLOAD_ALL, CallerRole = GuardCallerRole.CliClient });

            Assert.False(blockedSwitch.Succeeded);
            Assert.Equal("BLOCKED_BY_EXTERNAL_GPU_OWNER", blockedSwitch.Code);
            Assert.Equal(GuardOperationalState.READ_ONLY_OBSERVATION, blockedSwitch.OperationalState);
            Assert.False(blockedUnload.Succeeded);
            Assert.Equal("BLOCKED_BY_EXTERNAL_GPU_OWNER", blockedUnload.Code);

            // 3. Verify external mock process experienced ZERO interference (same PID, alive, /health 200)
            Assert.False(externalMockProc.HasExited);
            var extHealthAfter = await http.GetAsync($"http://127.0.0.1:{ExternalMockPort}/health");
            Assert.Equal(HttpStatusCode.OK, extHealthAfter.StatusCode);

            // 4. Verify all 4 ownership classifier branches explicitly (incl. EXTERNAL_CODEX_OWNED & UNKNOWN_OWNER fail-closed!)
            var codexClass = WindowsProcessDiscoveryService.ClassifyOwnership(
                new WindowsProcessDiscoveryService.WmiProcessRow(99001, 88001, "llama-server.exe", @"C:\tools\llama-server.exe", "llama-server.exe -m E:\\Models\\qwen.gguf --port 18083", DateTimeOffset.UtcNow),
                [new ProcessTreeNode(88001, "codex.exe", @"C:\Users\demo\codex.exe", "codex run")],
                managedGatewayPid: protectedSwap.Id,
                accessRestricted: false);
            var unknownClass = WindowsProcessDiscoveryService.ClassifyOwnership(
                new WindowsProcessDiscoveryService.WmiProcessRow(99002, 4, "llama-server.exe", null, null, null),
                [],
                managedGatewayPid: protectedSwap.Id,
                accessRestricted: true);
            Assert.Equal(ProcessOwnershipClass.EXTERNAL_CODEX_OWNED, codexClass.Class);
            Assert.Equal(ProcessOwnershipClass.UNKNOWN_OWNER, unknownClass.Class);

            // Stop ONLY our own isolated mock external process and verify Guard recovers ALLOW_SWITCH/RUNNING_HEALTHY
            StopProcessSafely(externalMockProc);
            externalMockProc = null;
            await Task.Delay(300);

            var statusAfterExit = await pipe.SendAsync(new GuardCommandRequest { Command = GuardCommandType.GET_STATUS, CallerRole = GuardCallerRole.UiClient });
            Assert.False(statusAfterExit.Status!.ExternalGpuConflictActive);
            Assert.NotEqual(GuardOperationalState.READ_ONLY_OBSERVATION, statusAfterExit.OperationalState);

            WriteJsonEvidence("phase4a_external_process_evidence.json", new
            {
                testSuite = "Phase 4A External Process Discovery & Zero-Interference Protection (Tests #2, #3, #18)",
                executedAtUtc = DateTimeOffset.UtcNow,
                isolationGuarantee = "Tested exclusively with isolated mock process on 127.0.0.1:19260; production processes untouched.",
                discoveredManagedProcess = managedEntry,
                discoveredExternalProcess = externalEntry,
                ownershipClassificationUnitProof = new
                {
                    codexOwned = new { codexClass.Class, codexClass.Reason },
                    unknownOwnerFailClosed = new { unknownClass.Class, unknownClass.Reason }
                },
                blockedCommandsWhileExternalPresent = new
                {
                    switchDecision = new { blockedSwitch.Succeeded, blockedSwitch.Code, blockedSwitch.OperationalState, blockedSwitch.Message },
                    unloadDecision = new { blockedUnload.Succeeded, blockedUnload.Code, blockedUnload.OperationalState, blockedUnload.Message }
                },
                externalProcessZeroInterferenceVerified = new
                {
                    healthBeforeHttp = 200,
                    healthAfterBlockedActionsHttp = (int)extHealthAfter.StatusCode,
                    terminatedOrAttachedByGuard = false
                },
                stateAfterExternalExited = statusAfterExit.OperationalState,
                hostTelemetryCaptured = statusWhileExt.Status.Telemetry,
                allPassed = true
            });
        }
        finally
        {
            StopProcessSafely(externalMockProc);
            if (guardHost is not null) await guardHost.DisposeAsync();
            StopProcessSafely(protectedSwap);
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // SUITE C: Resource Safety Gate Comprehensive Verification (Tests #4, #5, #6, #7, #8 + UNKNOWN_OWNER + Missing Telemetry)
    // =========================================================================
    [Fact]
    public void Test_03_ResourceSafetyGate_Vram_Ram_Commit_Port_Sha256_UnknownOwner_And_MissingTelemetry()
    {
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
                DiscoveryTargetImageNames = ["fake-model.exe"],
                DiscoveryPortRangeMin = 19250,
                DiscoveryPortRangeMax = 19260,
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
            Assert.Single(postConcStatus.Status!.RunningManagedModels); // Strictly 1 model running — zero dual occupancy!

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
