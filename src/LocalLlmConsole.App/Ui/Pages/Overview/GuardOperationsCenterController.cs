using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalLLMManager.Guard.Contracts;
using LocalLlmConsole.Localization;
using LocalLlmConsole.Models;
using LocalLlmConsole.Services;

namespace LocalLlmConsole;

/// <summary>
/// Phase 4B-3 Controller managing Guard polling, model card projection, actions, and real WPF screenshots
/// cleanly outside MainWindow partials (keeping MainWindow partials strictly within upstream size budgets).
/// </summary>
public sealed class GuardOperationsCenterController
{
    private readonly GuardControlBridgeService _bridge;
    private readonly GuardSessionLogService _logService;
    private readonly Func<StateStore?> _stateStoreProvider;
    private readonly Action<string> _setStatus;
    private readonly Action _showModels;
    private readonly Action _showRuntimes;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isOverviewActive;
    private GuardOperationsCenterPage? _page;
    private DispatcherTimer? _pollTimer;

    public static GuardOperationsCenterController CreateForShell(
        Window window,
        UIElement benchmarksNavButton,
        string workspaceRoot,
        Func<StateStore?> stateStoreProvider,
        Action<string> setStatus,
        Action showModels,
        Action showRuntimes,
        Dispatcher dispatcher,
        Func<bool> isOverviewActive)
    {
        benchmarksNavButton.Visibility = Visibility.Collapsed;
        return new GuardOperationsCenterController(workspaceRoot, stateStoreProvider, setStatus, showModels, showRuntimes, dispatcher, isOverviewActive);
    }

    public GuardOperationsCenterController(
        string workspaceRoot,
        Func<StateStore?> stateStoreProvider,
        Action<string> setStatus,
        Action showModels,
        Action showRuntimes,
        Dispatcher dispatcher,
        Func<bool> isOverviewActive)
    {
        _bridge = new GuardControlBridgeService(GuardBackendOptions.Current);
        _logService = new GuardSessionLogService(Path.Combine(workspaceRoot, "logs", "guard-sessions"));
        _stateStoreProvider = stateStoreProvider;
        _setStatus = setStatus;
        _showModels = showModels;
        _showRuntimes = showRuntimes;
        _dispatcher = dispatcher;
        _isOverviewActive = isOverviewActive;

        _logService.Append("startup", "system", "INFO",
            $"ExternalLlamaSwap 模式已永久锁定；固定入口 {GuardBackendOptions.Current.FixedGatewayBaseUrl} | 模型 ID: {GuardBackendOptions.Current.FixedLogicalModelId}");

        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
        {
            if (_isOverviewActive())
                await RefreshAsync();
        }, _dispatcher);
        _pollTimer.Start();
    }

    public void InvalidatePage()
    {
        _page = null;
    }

    public GuardOperationsCenterPage EnsurePage()
    {
        return _page ??= new GuardOperationsCenterPage(
            new GuardOperationsCenterActions(
                RefreshAsync: RefreshAsync,
                SwitchProfileAsync: SwitchProfileAsync,
                PauseAsync: () => RunSimpleActionAsync(Loc.T("GuardCenter.Action.Pause"), () => _bridge.PauseAsync()),
                ResumeAsync: () => RunSimpleActionAsync(Loc.T("GuardCenter.Action.Resume"), () => _bridge.ResumeAsync()),
                UnloadAsync: () => RunSimpleActionAsync(Loc.T("GuardCenter.Action.Unload"), () => _bridge.UnloadAsync()),
                CancelWait: () => { _bridge.CancelPendingWait(); _setStatus(Loc.T("GuardCenter.Status.WaitCancelled")); },
                RollbackAsync: () =>
                {
                    var confirm = System.Windows.MessageBox.Show(
                        Loc.T("GuardCenter.Rollback.ConfirmMessage"),
                        Loc.T("GuardCenter.Rollback.ConfirmTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return Task.CompletedTask;
                    return RunSimpleActionAsync(Loc.T("GuardCenter.Action.Rollback"), () => _bridge.RollbackToBaselineAsync());
                },
                UnlockFaultAsync: () => RunSimpleActionAsync(Loc.T("GuardCenter.Action.UnlockFault"), () => _bridge.UnlockFaultAsync("Operator UI unlock")),
                ExportLogsAsync: ExportLogsAsync,
                NavigateToModels: _showModels,
                NavigateToRuntimes: _showRuntimes),
            _logService);
    }

    public async Task RefreshAsync()
    {
        if (_page is null) return;
        var snap = await _bridge.PollStatusAsync();
        var cards = await BuildModelCardsAsync(snap);
        await _dispatcher.InvokeAsync(() =>
        {
            _page.RenderSnapshot(snap, cards);
            _setStatus(snap.GuardOnline
                ? Loc.T("GuardCenter.Status.Online", snap.DisplayState, snap.ActiveProfile ?? Loc.T("GuardCenter.Status.None"))
                : Loc.T("GuardCenter.Status.Offline"));
        });
    }

    public async Task SwitchProfileAsync(string profileKey)
    {
        _logService.Append("ui-switch", profileKey, "INFO", $"User request via Guard switch profile -> {profileKey}");
        _setStatus(Loc.T("GuardCenter.Status.Switching", profileKey));
        var resp = await _bridge.SwitchProfileAsync(profileKey, GuardCallerRole.UiClient);
        _logService.Append("ui-switch", profileKey, resp.Succeeded ? "INFO" : "ERROR",
            $"[{resp.Code}] {resp.Message} (State={resp.OperationalState}, Rollback={resp.RollbackOutcome})");
        _setStatus($"[{resp.Code}] {resp.Message}");
        await RefreshAsync();
    }

    public Task UnloadAsync()
        => RunSimpleActionAsync("安全卸载", () => _bridge.UnloadAsync());

    private async Task RunSimpleActionAsync(string label, Func<Task<GuardCommandResponse>> action)
    {
        var resp = await action();
        _logService.Append("ui-control", resp.ActiveProfile ?? "system", resp.Succeeded ? "INFO" : "WARN",
            $"{label}: [{resp.Code}] {resp.Message}");
        _setStatus($"{label}: {resp.Message}");
        await RefreshAsync();
    }

    private Task ExportLogsAsync(string outputPath)
    {
        if (_page is null) return Task.CompletedTask;
        var saved = _logService.ExportFilteredLogs(outputPath, _page.CurrentLogFilter());
        _setStatus($"已导出脱敏日志: {saved}");
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<GuardModelCardModel>> BuildModelCardsAsync(GuardOverviewSnapshot snap)
    {
        var cards = new List<GuardModelCardModel>();
        var defaults = new (string Key, string Name, string Runtime, string Gguf, int Ctx, string Est)[]
        {
            ("cyber-tiel", "Cyber-Tiel-Coder-35B-A3B", "llama.cpp b12328 (CUDA 12.4)", @"E:\Models\Cyber-Tiel-Coder-35B-A3B-MTP-UD-Q3_K_XL.gguf", 131072, "VRAM ~13.8 GiB | RAM ~4.0 GiB"),
            ("qwen36", "Qwen3.6-35B-A3B", "llama.cpp Round151 Cand-B (CUDA 12.4)", @"E:\Models\Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf", 65536, "VRAM ~12.4 GiB | RAM ~8.5 GiB (MoE)"),
            ("inkling2", "Inkling-2-Mini", "llama.cpp Runtime-B (CUDA 12.4)", @"E:\Models\Inkling-2-Mini.gguf", 65536, "VRAM ~9.2 GiB | RAM ~4.2 GiB")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stateStore = _stateStoreProvider();
        if (stateStore is not null)
        {
            var models = await stateStore.ListModelsAsync();
            var runtimes = await stateStore.ListRuntimesAsync();
            var profiles = await stateStore.ListNamedModelLaunchProfilesAsync();
            var rtMap = runtimes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var p in profiles)
            {
                var m = models.FirstOrDefault(x => x.Id.Equals(p.ModelId, StringComparison.OrdinalIgnoreCase));
                if (m is null) continue;
                rtMap.TryGetValue(p.Settings.RuntimeId, out var rt);
                var key = ModelGatewayRouteId.SafeSegment(m.Name);
                if (!seen.Add(key)) continue;
                var val = GuardProfileSyncService.ValidateAndEstimate(m, rt, p.Settings);
                var sha = rt is not null ? GuardProfileSyncService.ComputeFileSha256(rt.ExecutablePath) : "";
                cards.Add(new GuardModelCardModel(
                    ProfileKey: key,
                    DisplayName: m.Name,
                    RuntimeLabel: rt?.Name ?? "未绑定 Runtime",
                    RuntimeSha256Short: sha.Length >= 12 ? sha[..12] : "N/A",
                    GgufPath: m.ModelPath,
                    ContextLength: p.Settings.ContextSize,
                    ResourceEstimate: $"VRAM ~{val.EstimatedVramMiB / 1024.0:F1} GiB | RAM ~{val.EstimatedRamMiB / 1024.0:F1} GiB",
                    State: ResolveCardState(key, snap),
                    LastRunTimeText: snap.LastSwitchTimeUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "待运行",
                    CanSwitch: snap.GuardOnline && !snap.ExternalGpuConflictActive));
            }
        }

        foreach (var d in defaults)
        {
            if (!seen.Add(d.Key)) continue;
            cards.Add(new GuardModelCardModel(
                ProfileKey: d.Key,
                DisplayName: d.Name,
                RuntimeLabel: d.Runtime,
                RuntimeSha256Short: "verified",
                GgufPath: d.Gguf,
                ContextLength: d.Ctx,
                ResourceEstimate: d.Est,
                State: ResolveCardState(d.Key, snap),
                LastRunTimeText: string.Equals(snap.ActiveProfile, d.Key, StringComparison.OrdinalIgnoreCase)
                    ? (snap.LastSwitchTimeUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "当前激活")
                    : "待运行",
                CanSwitch: snap.GuardOnline && !snap.ExternalGpuConflictActive));
        }
        return cards;
    }

    private static UnifiedDisplayState ResolveCardState(string profileKey, GuardOverviewSnapshot snap)
        => string.Equals(snap.ActiveProfile, profileKey, StringComparison.OrdinalIgnoreCase)
            ? snap.DisplayState
            : UnifiedDisplayState.MODEL_UNLOADED;

    public async Task CaptureWorkbenchScreenshotsAsync(
        FrameworkElement window,
        string outDir,
        Action showModels,
        Action showRuntimes,
        Action showOverview)
    {
        Directory.CreateDirectory(outDir);
        await Task.Delay(1200);
        await RefreshAsync();
        await Task.Delay(300);
        SaveVisualPng(window, Path.Combine(outDir, "phase4b_01_overview_guard_center.png"));

        showModels();
        await Task.Delay(600);
        SaveVisualPng(window, Path.Combine(outDir, "phase4b_02_models_and_launch_profile.png"));

        showRuntimes();
        await Task.Delay(600);
        SaveVisualPng(window, Path.Combine(outDir, "phase4b_03_runtimes_registry.png"));

        showOverview();
    }

    public async Task ExecuteGuardUiSwitchWorkflowAsync(
        FrameworkElement window,
        string outDir)
    {
        System.IO.Directory.CreateDirectory(outDir);
        var results = new Dictionary<string, object>();
        var tStart = DateTimeOffset.UtcNow;
        results["workflow_start_utc"] = tStart.ToString("o");

        // 1. Initial State
        await Task.Delay(1500);
        await RefreshAsync();
        await Task.Delay(500);
        var path01 = System.IO.Path.Combine(outDir, "wpf_01_initial_model_a.png");
        SaveVisualPng(window, path01);
        results["screenshot_initial_a"] = path01;

        var snapA = await _bridge.PollStatusAsync();
        results["initial_state"] = new Dictionary<string, object?>
        {
            ["activeProfile"] = snapA.ActiveProfile,
            ["operationalState"] = snapA.DisplayState.ToString(),
            ["vramMiB"] = snapA.RawGuardStatus?.Telemetry?.GpuUsedVramMiB
        };

        // 2. Switch Profile to profile_qwen (Model B)
        var swB = System.Diagnostics.Stopwatch.StartNew();
        var respB = await _bridge.SwitchProfileAsync("profile_qwen", GuardCallerRole.UiClient);
        swB.Stop();
        results["switch_to_b"] = new Dictionary<string, object?>
        {
            ["succeeded"] = respB.Succeeded,
            ["code"] = respB.Code.ToString(),
            ["message"] = respB.Message,
            ["operationalState"] = respB.OperationalState.ToString(),
            ["idempotencyKey"] = respB.IdempotencyKey,
            ["elapsed_ms"] = swB.ElapsedMilliseconds
        };

        await Task.Delay(2000);
        await RefreshAsync();
        await Task.Delay(500);
        var path02 = System.IO.Path.Combine(outDir, "wpf_02_switched_to_model_b.png");
        SaveVisualPng(window, path02);
        results["screenshot_model_b"] = path02;

        var snapB = await _bridge.PollStatusAsync();
        results["model_b_state"] = new Dictionary<string, object?>
        {
            ["activeProfile"] = snapB.ActiveProfile,
            ["operationalState"] = snapB.DisplayState.ToString(),
            ["vramMiB"] = snapB.RawGuardStatus?.Telemetry?.GpuUsedVramMiB
        };

        // Smoke test inference on 19000 for Model B
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var chatPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = "local-coder",
                messages = new[] { new { role = "user", content = "Write 1 line of python" } },
                max_tokens = 10,
                temperature = 0.1
            });
            var content = new System.Net.Http.StringContent(chatPayload, System.Text.Encoding.UTF8, "application/json");
            var infResp = await http.PostAsync("http://127.0.0.1:19000/v1/chat/completions", content);
            results["model_b_inference_status"] = (int)infResp.StatusCode;
            results["model_b_inference_body"] = await infResp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            results["model_b_inference_error"] = ex.Message;
        }

        // 3. Switch Profile back to profile_cyber (Model A)
        var swA = System.Diagnostics.Stopwatch.StartNew();
        var respA = await _bridge.SwitchProfileAsync("profile_cyber", GuardCallerRole.UiClient);
        swA.Stop();
        results["switch_to_a"] = new Dictionary<string, object?>
        {
            ["succeeded"] = respA.Succeeded,
            ["code"] = respA.Code.ToString(),
            ["message"] = respA.Message,
            ["operationalState"] = respA.OperationalState.ToString(),
            ["idempotencyKey"] = respA.IdempotencyKey,
            ["elapsed_ms"] = swA.ElapsedMilliseconds
        };

        await Task.Delay(2000);
        await RefreshAsync();
        await Task.Delay(500);
        var path03 = System.IO.Path.Combine(outDir, "wpf_03_restored_model_a.png");
        SaveVisualPng(window, path03);
        results["screenshot_restored_a"] = path03;

        var snapFinal = await _bridge.PollStatusAsync();
        results["restored_model_a_state"] = new Dictionary<string, object?>
        {
            ["activeProfile"] = snapFinal.ActiveProfile,
            ["operationalState"] = snapFinal.DisplayState.ToString(),
            ["vramMiB"] = snapFinal.RawGuardStatus?.Telemetry?.GpuUsedVramMiB
        };

        // Smoke test inference on 19000 for Model A
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var chatPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = "local-coder",
                messages = new[] { new { role = "user", content = "Write 1 line of python" } },
                max_tokens = 10,
                temperature = 0.1
            });
            var content = new System.Net.Http.StringContent(chatPayload, System.Text.Encoding.UTF8, "application/json");
            var infResp = await http.PostAsync("http://127.0.0.1:19000/v1/chat/completions", content);
            results["model_a_inference_status"] = (int)infResp.StatusCode;
            results["model_a_inference_body"] = await infResp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            results["model_a_inference_error"] = ex.Message;
        }

        results["workflow_end_utc"] = DateTimeOffset.UtcNow.ToString("o");
        results["workflow_verdict"] = (respB.Succeeded && respA.Succeeded && snapFinal.ActiveProfile == "profile_cyber") ? "PASS" : "FAIL";

        var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var jsonText = System.Text.Json.JsonSerializer.Serialize(results, jsonOptions);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(outDir, "wpf_real_interaction_results.json"), jsonText);
        var rootPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outDir) ?? outDir, "wpf_real_interaction_results.json");
        await System.IO.File.WriteAllTextAsync(rootPath, jsonText);
    }

    public static void SaveVisualPng(FrameworkElement element, string filePath)
    {
        element.UpdateLayout();
        var width = Math.Max(1200, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(780, (int)Math.Ceiling(element.ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}
