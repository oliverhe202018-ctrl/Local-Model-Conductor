using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalLLMManager.Guard.Contracts;
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

    public GuardOperationsCenterPage EnsurePage()
    {
        return _page ??= new GuardOperationsCenterPage(
            new GuardOperationsCenterActions(
                RefreshAsync: RefreshAsync,
                SwitchProfileAsync: SwitchProfileAsync,
                PauseAsync: () => RunSimpleActionAsync("暂停推理", () => _bridge.PauseAsync()),
                ResumeAsync: () => RunSimpleActionAsync("恢复推理", () => _bridge.ResumeAsync()),
                UnloadAsync: () => RunSimpleActionAsync("安全卸载", () => _bridge.UnloadAsync()),
                CancelWait: () => { _bridge.CancelPendingWait(); _setStatus("已取消等待（运行中请求不受影响）"); },
                RollbackAsync: () => RunSimpleActionAsync("回滚健康基线", () => _bridge.RollbackToBaselineAsync()),
                UnlockFaultAsync: () => RunSimpleActionAsync("解除故障锁定", () => _bridge.UnlockFaultAsync("Operator UI unlock")),
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
                ? $"Guard 在线 | 状态: {snap.DisplayState} | 激活 Profile: {snap.ActiveProfile ?? "无"}"
                : "Guard 离线 — 自动进入只读观察模式 (READ_ONLY_OBSERVATION)");
        });
    }

    public async Task SwitchProfileAsync(string profileKey)
    {
        _logService.Append("ui-switch", profileKey, "INFO", $"用户请求通过 Guard 切换 Profile -> {profileKey}");
        _setStatus($"正在通过 Guard 切换至 {profileKey}...");
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
