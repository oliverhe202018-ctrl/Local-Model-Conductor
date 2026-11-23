using System.Windows;
using System.Windows.Controls;
using LocalLLMManager.Guard.Contracts;
using LocalLlmConsole.Localization;
using LocalLlmConsole.Services;
using WpfButton = System.Windows.Controls.Button;

namespace LocalLlmConsole;

public sealed partial class GuardOperationsCenterPage
{
    private Border BuildMultiModelCardsSection()
    {
        var card = CreateCard();
        var stack = new StackPanel();
        stack.Children.Add(SectionHeading(Loc.T("GuardCenter.Section.ModelCards")));
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

        if (!snap.GuardOnline || snap.ExternalGpuConflictActive || snap.DisplayState is UnifiedDisplayState.FAULT_LOCKED or UnifiedDisplayState.RECOVERY_REQUIRED or UnifiedDisplayState.ROLLBACK_FAILED)
        {
            _warningBanner.Visibility = Visibility.Visible;
            _warningText.Text = snap.DisplayState == UnifiedDisplayState.FAULT_LOCKED
                ? Loc.T("GuardCenter.Warning.FaultLocked", snap.FaultReason ?? string.Empty)
                : snap.DisplayState == UnifiedDisplayState.RECOVERY_REQUIRED
                    ? Loc.T("GuardCenter.Warning.RecoveryRequired", snap.FaultReason ?? string.Empty)
                    : snap.DisplayState == UnifiedDisplayState.ROLLBACK_FAILED
                        ? Loc.T("GuardCenter.Warning.RollbackFailed", snap.FaultReason ?? string.Empty)
                        : !snap.GuardOnline
                            ? Loc.T("GuardCenter.Warning.GuardOffline", snap.FaultReason ?? string.Empty)
                            : Loc.T("GuardCenter.Warning.ExternalConflict", snap.ExternalConflictSummary ?? string.Empty);
        }
        else
        {
            _warningBanner.Visibility = Visibility.Collapsed;
        }

        _endpointSummary.Text = Loc.T("GuardCenter.EndpointSummaryRuntime",
            snap.FixedGatewayBaseUrl,
            snap.FixedLogicalModelId,
            snap.RawGuardStatus?.GuardPid.ToString() ?? "N/A",
            snap.RawGuardStatus?.LlamaSwapGatewayPid?.ToString() ?? "N/A");

        var activeCard = cards.FirstOrDefault(c => string.Equals(c.ProfileKey, snap.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        var activeModelLabel = string.IsNullOrWhiteSpace(snap.ActiveModelId)
            ? (string.IsNullOrWhiteSpace(snap.ActiveProfile) ? Loc.T("GuardCenter.Model.Unloaded") : Loc.T("GuardCenter.Model.ProfileActive", snap.ActiveProfile))
            : $"{snap.ActiveModelId}  [Profile: {snap.ActiveProfile}]";
        _modelInfoText.Text = activeModelLabel;
        _runtimeInfoText.Text = activeCard is not null
            ? $"{activeCard.RuntimeLabel} (SHA256: {activeCard.RuntimeSha256Short})"
            : Loc.T("GuardCenter.Model.RuntimeUnbound");
        _ggufPathText.Text = activeCard?.GgufPath ?? "—";
        _contextText.Text = activeCard is not null ? $"{activeCard.ContextLength:N0} tokens" : "—";

        var tel = snap.RawGuardStatus?.Telemetry;
        _gpuText.Text = tel is { GpuAvailable: true }
            ? Loc.T("GuardCenter.Telemetry.GpuFormat", tel.GpuName ?? string.Empty, tel.GpuUsedVramMiB, tel.GpuTotalVramMiB, tel.GpuFreeVramMiB, tel.GpuUtilizationPct)
            : Loc.T("GuardCenter.Telemetry.GpuUnavailable");
        _ramCommitText.Text = tel is { RamAvailable: true }
            ? Loc.T("GuardCenter.Telemetry.RamCommitFormat", tel.RamAvailableMiB, tel.RamTotalMiB, tel.CommitAvailableMiB, tel.CommitLimitMiB)
            : Loc.T("GuardCenter.Telemetry.RamUnavailable");

        _perfMetricsText.Text = Loc.T("GuardCenter.Telemetry.PerfMetricsFormat",
            decodeTps.HasValue ? $"{decodeTps:F1}" : "N/A",
            prefillTps.HasValue ? $"{prefillTps:F1}" : "N/A",
            ttftMs.HasValue ? $"{ttftMs:F0}" : "N/A");
        _queueText.Text = Loc.T("GuardCenter.Queue.StatusFormat", snap.InFlightRequestCount, snap.QueuedRequestCount);
        _lastSwitchText.Text = snap.LastSwitchTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Loc.T("GuardCenter.Switch.Never");
        _rollbackText.Text = snap.RollbackStatusText;

        _loadingBar.Value = snap.LoadingProgressPct;
        _loadingPctText.Text = $"{snap.LoadingProgressPct:F0}%";
        var stageDisplayName = (snap.SwitchStage.ActiveStageIndex >= 0 && snap.SwitchStage.ActiveStageIndex < 8)
            ? Loc.T($"GuardCenter.Stage.{snap.SwitchStage.ActiveStageIndex + 1}")
            : snap.SwitchStage.StageName;
        _failureReasonText.Text = Loc.T("GuardCenter.Stage.CurrentFormat", stageDisplayName, snap.SwitchStage.Detail) +
            (string.IsNullOrWhiteSpace(snap.FaultReason) ? "" : Loc.T("GuardCenter.Stage.FaultReasonFormat", snap.FaultReason));

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
                or UnifiedDisplayState.GUARD_OFFLINE
                or UnifiedDisplayState.FAULT_LOCKED
                or UnifiedDisplayState.RECOVERY_REQUIRED
                or UnifiedDisplayState.ROLLBACK_FAILED
                or UnifiedDisplayState.MODEL_ERROR
                or UnifiedDisplayState.DRAINING
                or UnifiedDisplayState.MODEL_DRAINING
                or UnifiedDisplayState.LOADING
                or UnifiedDisplayState.MODEL_LOADING
                or UnifiedDisplayState.ROLLING_BACK);

        foreach (var btn in _mutatingButtons)
        {
            btn.IsEnabled = canMutate;
            btn.Opacity = canMutate ? 1.0 : 0.4;
        }
        _unlockFaultButton.IsEnabled = snap.GuardOnline && snap.DisplayState is UnifiedDisplayState.FAULT_LOCKED or UnifiedDisplayState.RECOVERY_REQUIRED;
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
            System.Windows.Automation.AutomationProperties.SetAutomationId(border, $"Card_{card.ProfileKey}");
            System.Windows.Automation.AutomationProperties.SetName(border, $"Model Card: {card.DisplayName}");

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

            stack.Children.Add(CardLine(Loc.T("GuardCenter.Card.Runtime", card.RuntimeLabel, card.RuntimeSha256Short)));
            stack.Children.Add(CardLine($"GGUF: {card.GgufPath}"));
            stack.Children.Add(CardLine(Loc.T("GuardCenter.Card.ContextResource", card.ContextLength.ToString("N0"), card.ResourceEstimate)));
            stack.Children.Add(CardLine(Loc.T("GuardCenter.Card.LastRun", card.LastRunTimeText)));

            var switchable = guardAllowsSwitch && card.CanSwitch;
            var btn = new WpfButton
            {
                Content = isVerified ? Loc.T("GuardCenter.Card.CurrentActive") : Loc.T("GuardCenter.Card.SwitchToThis"),
                IsEnabled = switchable && !isVerified,
                Opacity = (switchable && !isVerified) ? 1.0 : 0.45,
                Background = BrushOf("#1F6FEB"),
                Foreground = BrushOf("#FFFFFF"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, $"BtnSwitch_{card.ProfileKey}");
            System.Windows.Automation.AutomationProperties.SetName(btn, isVerified ? $"Current Active Model: {card.DisplayName}" : $"Switch to {card.DisplayName}");
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
            UnifiedDisplayState.RUNNING or UnifiedDisplayState.MODEL_INFERENCE_VERIFIED => (Loc.T("GuardCenter.State.RunningVerified"), "#132E21", "#3FB950", "#7EE787"),
            UnifiedDisplayState.MODEL_READY => (Loc.T("GuardCenter.State.Ready"), "#132E21", "#2EA043", "#7EE787"),
            UnifiedDisplayState.IDLE or UnifiedDisplayState.MODEL_UNLOADED => (Loc.T("GuardCenter.State.Idle"), "#1C2430", "#6E7681", "#C9D1D9"),
            UnifiedDisplayState.PROFILE_ACTIVE => (Loc.T("GuardCenter.State.ProfileActive"), "#142B44", "#58A6FF", "#79C0FF"),
            UnifiedDisplayState.LOADING or UnifiedDisplayState.MODEL_LOADING => (Loc.T("GuardCenter.State.Loading"), "#142B44", "#58A6FF", "#79C0FF"),
            UnifiedDisplayState.DRAINING or UnifiedDisplayState.MODEL_DRAINING => (Loc.T("GuardCenter.State.Draining"), "#2B213D", "#A371F7", "#D2A8FF"),
            UnifiedDisplayState.ROLLING_BACK => (Loc.T("GuardCenter.State.RollingBack"), "#3A2C0F", "#D29922", "#F2CC60"),
            UnifiedDisplayState.ROLLBACK_FAILED or UnifiedDisplayState.MODEL_ERROR => (Loc.T("GuardCenter.State.RollbackFailed"), "#421519", "#F85149", "#FF7B72"),
            UnifiedDisplayState.FAULT_LOCKED => (Loc.T("GuardCenter.State.FaultLocked"), "#421519", "#F85149", "#FF7B72"),
            UnifiedDisplayState.RECOVERY_REQUIRED => (Loc.T("GuardCenter.State.RecoveryRequired"), "#45220E", "#D29922", "#F2CC60"),
            UnifiedDisplayState.GUARD_OFFLINE => (Loc.T("GuardCenter.State.GuardOffline"), "#2D1B22", "#E5534B", "#FFA198"),
            _ => (guardOnline ? Loc.T("GuardCenter.State.ReadOnlyObservation") : Loc.T("GuardCenter.State.GuardOffline"), "#3B2314", "#F0883E", "#FFA657")
        };

        _stateBadgeText.Text = label;
        _stateBadgeText.Foreground = BrushOf(fg);
        _stateBadgeBorder.Background = BrushOf(bg);
        _stateBadgeBorder.BorderBrush = BrushOf(border);
    }
}
