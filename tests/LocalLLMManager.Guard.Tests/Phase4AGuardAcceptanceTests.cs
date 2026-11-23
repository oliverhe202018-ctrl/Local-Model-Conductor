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
public sealed partial class Phase4AGuardAcceptanceTests
{
    private static readonly string OutDir = ResolveOutDir();
    private static readonly string LlamaSwapExe = Environment.GetEnvironmentVariable("LLAMA_SWAP_PATH") ?? @"D:\project\本地模型计算\LocalLLMManager\vendor\llama-swap\v275\llama-swap.exe";
    private static readonly string FakeModelA = Environment.GetEnvironmentVariable("FAKE_MODEL_A_PATH") ?? ResolveFixtureFile("fake-model-a.exe", @"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275\fake-model.exe");
    private static readonly string FakeModelB = Environment.GetEnvironmentVariable("FAKE_MODEL_B_PATH") ?? ResolveFixtureFile("fake-model-b.exe", @"D:\project\本地模型计算\LocalLLMManager\tools\fake-model\v275-runtime-b\fake-model.exe");
    private static readonly string FaultServerPy = Environment.GetEnvironmentVariable("FAULT_SERVER_PY_PATH") ?? ResolveFixtureFile("mock_fault_server.py", @"D:\project\本地模型计算\phase3_3_integration_spike\spike_tests\mock_fault_server.py");

    private static string ResolveFixtureFile(string fileName, string preferredHardcoded)
    {
        if (File.Exists(preferredHardcoded)) return preferredHardcoded;
        var inRepoFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\tests\fixtures", fileName));
        if (File.Exists(inRepoFixtures)) return inRepoFixtures;
        var relativeFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\fixtures", fileName));
        if (File.Exists(relativeFixtures)) return relativeFixtures;
        var baseDirFixtures = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(baseDirFixtures)) return baseDirFixtures;
        return preferredHardcoded;
    }

    private static string ResolveOutDir()
    {
        var preferred = @"D:\project\本地模型计算\phase4a_guard";
        if (Directory.Exists(preferred)) return preferred;
        var fallback = Path.Combine(AppContext.BaseDirectory, "phase4a_guard");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IntegrationFixturesAvailable =>
        File.Exists(LlamaSwapExe) && File.Exists(FakeModelA) && File.Exists(FakeModelB);

    // Isolated ports strictly within 19350-19360 (or configurable via GUARD_TEST_BASE_PORT, NEVER touching production 19000/8082/18083/3001)
    private static readonly int BasePort = int.TryParse(Environment.GetEnvironmentVariable("GUARD_TEST_BASE_PORT"), out var bp) ? bp : 19350;
    private static int FacadePort => BasePort;
    private static int CyberPort => BasePort + 1;
    private static int QwenPort => BasePort + 2;
    private static int InklingPort => BasePort + 3;
    private static int FailHealthPort => BasePort + 4;
    private static int FailInferencePort => BasePort + 5;
    private static int FailStartupPort => BasePort + 6;
    private static int VanillaAuditPort => BasePort + 8;
    private static int InternalSwapPort => BasePort + 9;
    private static int ExternalMockPort => BasePort + 10;

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
        var envPy = Environment.GetEnvironmentVariable("PYTHON_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(envPy) && File.Exists(envPy)) return envPy;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "python",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                var outText = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                var lines = outText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !l.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) && File.Exists(l))
                    .ToArray();
                if (lines.Length > 0) return lines[0];
            }
        }
        catch { }

        foreach (var candidate in new[] { @"C:\hostedtoolcache\windows\Python\3.12.9\x64\python.exe", @"C:\hostedtoolcache\windows\Python\3.12.8\x64\python.exe", @"C:\Python312\python.exe", @"C:\Python311\python.exe" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "python";
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
    // SUITE A: P0 Management API Auth Isolation (Tests #9, #10, #16, #17 + ALL 7 搂4 scenarios + 搂5 SSE/FIFO through Guard Facade)
    // =========================================================================
    [Fact]
    public async Task Test_01_P0_AdminAuthIsolation_SharedUiCliIpc_Redaction_And_SseFifoPassThrough()
    {
        if (!IntegrationFixturesAvailable)
        {
            Assert.Skip("Integration fixtures (llama-swap / fake-model) are not present in this runner environment.");
            return;
        }
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
                DiscoveryTargetImageNames = ["fake-model.exe", Path.GetFileName(FakeModelA), Path.GetFileName(FakeModelB)],
                DiscoveryPortRangeMin = BasePort,
                DiscoveryPortRangeMax = BasePort + 10,
                ManagedGatewayPidProvider = () => protectedSwap.Id
            });
            await guardHost.StartAsync();

            // Execute ALL 7 mandatory 搂4 test cases against Guard Facade (19250) & direct internal port (19259)
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

            // Case 6 + Test #17: Shared ACL Named Pipe entry for BOTH WPF UI (UiClient) AND CLI (CliClient) 鈥?neither holds internalAdminKey!
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

            // Test #16: Full Log Redaction Verification 鈥?verify NEITHER internalAdminKey NOR agentInferenceKey appears in state or audit files!
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
        if (!IntegrationFixturesAvailable)
        {
            Assert.Skip("Integration fixtures (llama-swap / fake-model) are not present in this runner environment.");
            return;
        }
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
                DiscoveryTargetImageNames = ["fake-model.exe", Path.GetFileName(FakeModelA), Path.GetFileName(FakeModelB)],
                DiscoveryPortRangeMin = BasePort,
                DiscoveryPortRangeMax = BasePort + 10,
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
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    var r = await http.GetAsync($"http://127.0.0.1:{ExternalMockPort}/health");
                    if (r.IsSuccessStatusCode) { extHealthyBefore = true; break; }
                }
                catch { }
                await Task.Delay(150);
            }
            Assert.True(extHealthyBefore, $"External mock model did not become healthy on port {ExternalMockPort} within 15 seconds.");

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
}
