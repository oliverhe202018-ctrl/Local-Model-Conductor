using System.Windows;
using System.Windows.Controls;
using LocalLLMManager.Guard.Contracts;
using LocalLlmConsole.Services;
using WpfButton = System.Windows.Controls.Button;

namespace LocalLlmConsole;

public sealed partial class GuardOperationsCenterPage
{
    private Border BuildMultiModelCardsSection()
    {
        var card = CreateCard();
        var stack = new StackPanel();
        stack.Children.Add(SectionHeading("多模型管理（固定 127.0.0.1:19000/v1 | local-coder — 支持扩展任意模型）"));
        _cardsPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
        stack.Children.Add(_cardsPanel);
        card.Child = stack;
        return card;
    }

    public void RenderSnapshot(
        GuardOverviewSnapshot snap,
        IReadOnlyList<GuardModelCardModel> cards,
        double? decodeTps = null,
        double? prefillTps = null,
        double? ttftMs = null)
    {
        ApplyStateBadge(snap.DisplayState, snap.GuardOnline);

        if (!snap.GuardOnline || snap.ExternalGpuConflictActive || snap.DisplayState == UnifiedDisplayState.FAULT_LOCKED)
        {
            _warningBanner.Visibility = Visibility.Visible;
            _warningText.Text = snap.DisplayState == UnifiedDisplayState.FAULT_LOCKED
                ? $"[故障锁定 FAULT_LOCKED] {snap.FaultReason}"
                : !snap.GuardOnline
                    ? $"[Guard 离线 — 自动只读观察] {snap.FaultReason}"
                    : $"[外部模型占卡警告 — 只读观察] 检测到外部非托管模型 ({snap.ExternalConflictSummary})，已强制锁定全部 GPU 竞争操作，严禁干扰外部进程。";
        }
        else
        {
            _warningBanner.Visibility = Visibility.Collapsed;
        }

        _endpointSummary.Text = $"固定 API: {snap.FixedGatewayBaseUrl}  |  固定模型 ID: {snap.FixedLogicalModelId}  |  Guard PID: {(snap.RawGuardStatus?.GuardPid.ToString() ?? "离线")}  |  llama-swap PID: {(snap.RawGuardStatus?.LlamaSwapGatewayPid?.ToString() ?? "N/A")}";

        var activeCard = cards.FirstOrDefault(c => string.Equals(c.ProfileKey, snap.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        var activeModelLabel = string.IsNullOrWhiteSpace(snap.ActiveModelId)
            ? (string.IsNullOrWhiteSpace(snap.ActiveProfile) ? "未加载 (MODEL_UNLOADED)" : $"Profile 已选中但未加载: {snap.ActiveProfile} (PROFILE_ACTIVE)")
            : $"{snap.ActiveModelId}  [Profile: {snap.ActiveProfile}]";
        _modelInfoText.Text = activeModelLabel;
        _runtimeInfoText.Text = activeCard is not null
            ? $"{activeCard.RuntimeLabel} (SHA256: {activeCard.RuntimeSha256Short})"
            : "待绑定 Runtime";
        _ggufPathText.Text = activeCard?.GgufPath ?? "—";
        _contextText.Text = activeCard is not null ? $"{activeCard.ContextLength:N0} tokens" : "—";

        var tel = snap.RawGuardStatus?.Telemetry;
        _gpuText.Text = tel is { GpuAvailable: true }
            ? $"{tel.GpuName} — {tel.GpuUsedVramMiB:F0} / {tel.GpuTotalVramMiB:F0} MiB (空闲 {tel.GpuFreeVramMiB:F0} MiB | 利用率 {tel.GpuUtilizationPct:F0}%)"
            : "GPU 遥测不可用 (Fail-Closed)";
        _ramCommitText.Text = tel is { RamAvailable: true }
            ? $"RAM 可用 {tel.RamAvailableMiB:F0} / {tel.RamTotalMiB:F0} MiB  |  Commit 可用 {tel.CommitAvailableMiB:F0} / {tel.CommitLimitMiB:F0} MiB"
            : "RAM / Commit 遥测不可用";

        _perfMetricsText.Text = $"Decode: {(decodeTps.HasValue ? $"{decodeTps:F1} tok/s" : "N/A")}  |  Prefill: {(prefillTps.HasValue ? $"{prefillTps:F1} tok/s" : "N/A")}  |  TTFT: {(ttftMs.HasValue ? $"{ttftMs:F0} ms" : "N/A")}";
        _queueText.Text = $"活动请求 (In-Flight): {snap.InFlightRequestCount}  |  排队请求 (FIFO): {snap.QueuedRequestCount}";
        _lastSwitchText.Text = snap.LastSwitchTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未切换";
        _rollbackText.Text = snap.RollbackStatusText;

        _loadingBar.Value = snap.LoadingProgressPct;
        _loadingPctText.Text = $"{snap.LoadingProgressPct:F0}%";
        _failureReasonText.Text = $"当前阶段: {snap.SwitchStage.StageName} — {snap.SwitchStage.Detail}" +
            (string.IsNullOrWhiteSpace(snap.FaultReason) ? "" : $"\n故障/门禁说明: {snap.FaultReason}");

        for (int i = 0; i < _stagePills.Length; i++)
        {
            var isActive = i == snap.SwitchStage.ActiveStageIndex;
            var isDone = i < snap.SwitchStage.ActiveStageIndex && !snap.SwitchStage.Failed;
            _stagePills[i].Background = isActive
                ? BrushOf(snap.SwitchStage.Failed ? "#8E2525" : "#1F6FEB")
                : isDone ? BrushOf("#1E4620") : BrushOf("#1C2430");
            if (_stagePills[i].Child is TextBlock tb)
                tb.Foreground = (isActive || isDone) ? BrushOf("#FFFFFF") : BrushOf("#8B949E");
        }

        var canMutate = snap.GuardOnline
            && !snap.ExternalGpuConflictActive
            && snap.DisplayState is not (UnifiedDisplayState.READ_ONLY_OBSERVATION
                or UnifiedDisplayState.FAULT_LOCKED
                or UnifiedDisplayState.MODEL_DRAINING
                or UnifiedDisplayState.MODEL_LOADING
                or UnifiedDisplayState.ROLLING_BACK);

        foreach (var btn in _mutatingButtons)
        {
            btn.IsEnabled = canMutate;
            btn.Opacity = canMutate ? 1.0 : 0.4;
        }
        _unlockFaultButton.IsEnabled = snap.GuardOnline && snap.DisplayState == UnifiedDisplayState.FAULT_LOCKED;
        _unlockFaultButton.Opacity = _unlockFaultButton.IsEnabled ? 1.0 : 0.4;

        RenderModelCards(cards, canMutate);
        RefreshLogView();
    }

    private void RenderModelCards(IReadOnlyList<GuardModelCardModel> cards, bool guardAllowsSwitch)
    {
        _cardsPanel.Children.Clear();
        foreach (var card in cards)
        {
            var isVerified = card.State is UnifiedDisplayState.MODEL_INFERENCE_VERIFIED or UnifiedDisplayState.MODEL_READY;
            var border = new Border
            {
                Width = 318,
                Background = BrushOf("#101620"),
                BorderBrush = BrushOf(isVerified ? "#3FB950" : card.State == UnifiedDisplayState.PROFILE_ACTIVE ? "#58A6FF" : "#263244"),
                BorderThickness = new Thickness(isVerified ? 1.5 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 10, 10)
            };

            var stack = new StackPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var title = new TextBlock { Text = card.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#F0F6FC") };
            var badge = new Border
            {
                Background = BrushOf(isVerified ? "#132E21" : "#1C2430"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock { Text = card.State.ToString(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = BrushOf(isVerified ? "#7EE787" : "#79C0FF") }
            };
            DockPanel.SetDock(badge, Dock.Right);
            top.Children.Add(badge);
            top.Children.Add(title);
            stack.Children.Add(top);

            stack.Children.Add(CardLine($"Runtime: {card.RuntimeLabel} ({card.RuntimeSha256Short})"));
            stack.Children.Add(CardLine($"GGUF: {card.GgufPath}"));
            stack.Children.Add(CardLine($"上下文: {card.ContextLength:N0}  |  预估: {card.ResourceEstimate}"));
            stack.Children.Add(CardLine($"最近运行: {card.LastRunTimeText}"));

            var switchable = guardAllowsSwitch && card.CanSwitch;
            var btn = new WpfButton
            {
                Content = isVerified ? "当前运行中 (已验收)" : "通过 Guard 切换此模型",
                IsEnabled = switchable && !isVerified,
                Opacity = (switchable && !isVerified) ? 1.0 : 0.45,
                Background = BrushOf("#1F6FEB"),
                Foreground = BrushOf("#FFFFFF"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12
            };
            var profileKey = card.ProfileKey;
            btn.Click += (_, _) => _ = _actions.SwitchProfileAsync(profileKey);
            stack.Children.Add(btn);

            border.Child = stack;
            _cardsPanel.Children.Add(border);
        }
    }

    private static TextBlock CardLine(string text) => new()
    {
        Text = text,
        FontSize = 11.2,
        Foreground = BrushOf("#C9D1D9"),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 1.5, 0, 1.5)
    };

    private void ApplyStateBadge(UnifiedDisplayState state, bool guardOnline)
    {
        var (label, bg, border, fg) = state switch
        {
            UnifiedDisplayState.MODEL_INFERENCE_VERIFIED => ("MODEL_INFERENCE_VERIFIED · 已推理验收", "#132E21", "#3FB950", "#7EE787"),
            UnifiedDisplayState.MODEL_READY => ("MODEL_READY · 已就绪待验收", "#132E21", "#2EA043", "#7EE787"),
            UnifiedDisplayState.PROFILE_ACTIVE => ("PROFILE_ACTIVE · 仅选中未加载", "#142B44", "#58A6FF", "#79C0FF"),
            UnifiedDisplayState.MODEL_LOADING => ("MODEL_LOADING · 正在加载", "#142B44", "#58A6FF", "#79C0FF"),
            UnifiedDisplayState.MODEL_DRAINING => ("MODEL_DRAINING · 请求排空中", "#2B213D", "#A371F7", "#D2A8FF"),
            UnifiedDisplayState.ROLLING_BACK => ("ROLLING_BACK · 自动回滚中", "#3A2C0F", "#D29922", "#F2CC60"),
            UnifiedDisplayState.FAULT_LOCKED => ("FAULT_LOCKED · 双重故障锁定", "#421519", "#F85149", "#FF7B72"),
            UnifiedDisplayState.MODEL_ERROR => ("MODEL_ERROR · 模型异常", "#421519", "#F85149", "#FF7B72"),
            UnifiedDisplayState.MODEL_UNLOADED => ("MODEL_UNLOADED · 未加载", "#1C2430", "#6E7681", "#C9D1D9"),
            _ => (guardOnline ? "READ_ONLY_OBSERVATION · 只读观察" : "READ_ONLY_OBSERVATION · Guard 离线", "#3B2314", "#F0883E", "#FFA657")
        };

        _stateBadgeText.Text = label;
        _stateBadgeText.Foreground = BrushOf(fg);
        _stateBadgeBorder.Background = BrushOf(bg);
        _stateBadgeBorder.BorderBrush = BrushOf(border);
    }
}
