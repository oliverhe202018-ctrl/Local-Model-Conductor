using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalLLMManager.Guard.Contracts;
using LocalLlmConsole.Localization;
using LocalLlmConsole.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LocalLlmConsole;

public sealed record GuardModelCardModel(
    string ProfileKey,
    string DisplayName,
    string RuntimeLabel,
    string RuntimeSha256Short,
    string GgufPath,
    int ContextLength,
    string ResourceEstimate,
    UnifiedDisplayState State,
    string LastRunTimeText,
    bool CanSwitch);

public sealed record GuardOperationsCenterActions(
    Func<Task> RefreshAsync,
    Func<string, Task> SwitchProfileAsync,
    Func<Task> PauseAsync,
    Func<Task> ResumeAsync,
    Func<Task> UnloadAsync,
    Action CancelWait,
    Func<Task> RollbackAsync,
    Func<Task> UnlockFaultAsync,
    Func<string, Task> ExportLogsAsync,
    Action NavigateToModels,
    Action NavigateToRuntimes);

/// <summary>
/// Phase 4B-3 Unified Operations Center embedded as the Home Overview page inside MainWindow
/// when running in ExternalLlamaSwap + LocalLLMManager.Guard mode.
/// </summary>
public sealed partial class GuardOperationsCenterPage
{
    private readonly GuardOperationsCenterActions _actions;
    private readonly GuardSessionLogService _logService;

    private readonly Border _stateBadgeBorder = new();
    private readonly TextBlock _stateBadgeText = new();
    private readonly Border _warningBanner = new();
    private readonly TextBlock _warningText = new();
    private readonly TextBlock _endpointSummary = new();
    private readonly TextBlock _modelInfoText = new();
    private readonly TextBlock _runtimeInfoText = new();
    private readonly TextBlock _ggufPathText = new();
    private readonly TextBlock _contextText = new();
    private readonly TextBlock _gpuText = new();
    private readonly TextBlock _ramCommitText = new();
    private readonly TextBlock _perfMetricsText = new();
    private readonly TextBlock _queueText = new();
    private readonly TextBlock _lastSwitchText = new();
    private readonly TextBlock _rollbackText = new();
    private readonly TextBlock _failureReasonText = new();
    private readonly WpfProgressBar _loadingBar = new();
    private readonly TextBlock _loadingPctText = new();

    private readonly Border[] _stagePills = new Border[8];
    private readonly WrapPanel _cardsPanel = new();
    private readonly List<WpfButton> _mutatingButtons = [];
    private WpfButton _unlockFaultButton = new();

    private readonly WpfTextBox _logBox = new();
    private readonly WpfTextBox _logSearchBox = new();
    private readonly WpfCheckBox _pauseScrollCheck = new();
    private readonly WpfCheckBox _errorOnlyCheck = new();
    private readonly WpfCheckBox _cudaOomCheck = new();
    private readonly WpfComboBox _modelFilterCombo = new();
    private readonly WpfComboBox _sessionFilterCombo = new();

    public ScrollViewer Content { get; }

    public GuardOperationsCenterPage(GuardOperationsCenterActions actions, GuardSessionLogService logService)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 20) };
        root.Children.Add(BuildHeaderCard());
        root.Children.Add(BuildWarningBanner());
        root.Children.Add(BuildOverviewMetricsGrid());
        root.Children.Add(BuildLifecycleControlCard());
        root.Children.Add(BuildMultiModelCardsSection());
        root.Children.Add(BuildLiveTerminalSection());

        System.Windows.Automation.AutomationProperties.SetAutomationId(_stateBadgeText, "StateBadgeText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_endpointSummary, "EndpointSummaryText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_modelInfoText, "ModelInfoText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_gpuText, "GpuTelemetryText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_ramCommitText, "RamCommitText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_unlockFaultButton, "BtnUnlockFault");

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
    }

    private Border BuildHeaderCard()
    {
        var card = CreateCard();
        var dock = new DockPanel();

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = Loc.T("GuardCenter.Title"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf("#F0F6FC")
        });
        _endpointSummary.Text = Loc.T("GuardCenter.EndpointSummary", GuardBackendOptions.Current.FixedGatewayBaseUrl, GuardBackendOptions.Current.FixedLogicalModelId);
        _endpointSummary.FontSize = 12;
        _endpointSummary.Foreground = BrushOf("#8B949E");
        _endpointSummary.Margin = new Thickness(0, 4, 0, 0);
        left.Children.Add(_endpointSummary);
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        _stateBadgeBorder.CornerRadius = new CornerRadius(6);
        _stateBadgeBorder.BorderThickness = new Thickness(1.5);
        _stateBadgeBorder.Padding = new Thickness(14, 6, 14, 6);
        _stateBadgeBorder.VerticalAlignment = VerticalAlignment.Center;
        _stateBadgeText.FontSize = 12.5;
        _stateBadgeText.FontWeight = FontWeights.Bold;
        _stateBadgeBorder.Child = _stateBadgeText;
        DockPanel.SetDock(_stateBadgeBorder, Dock.Right);
        dock.Children.Add(_stateBadgeBorder);

        card.Child = dock;
        return card;
    }

    private Border BuildWarningBanner()
    {
        _warningBanner.Background = BrushOf("#3B1D14");
        _warningBanner.BorderBrush = BrushOf("#F85149");
        _warningBanner.BorderThickness = new Thickness(1.5);
        _warningBanner.CornerRadius = new CornerRadius(6);
        _warningBanner.Padding = new Thickness(14, 9, 14, 9);
        _warningBanner.Margin = new Thickness(0, 0, 0, 10);
        _warningBanner.Visibility = Visibility.Collapsed;

        _warningText.Foreground = BrushOf("#FFDCD7");
        _warningText.FontSize = 12;
        _warningText.TextWrapping = TextWrapping.Wrap;
        _warningBanner.Child = _warningText;
        return _warningBanner;
    }

    private Grid BuildOverviewMetricsGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

        var leftCard = CreateCard(margin: new Thickness(0, 0, 6, 0));
        var leftStack = new StackPanel();
        leftStack.Children.Add(SectionHeading(Loc.T("GuardCenter.Section.Overview")));
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.ModelInfo"), _modelInfoText);
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.RuntimeVersion"), _runtimeInfoText);
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.GgufPath"), _ggufPathText);
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.ContextLength"), _contextText);
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.QueueStatus"), _queueText);
        AddMetricRow(leftStack, Loc.T("GuardCenter.Metric.LastSwitchTime"), _lastSwitchText);
        leftCard.Child = leftStack;
        Grid.SetColumn(leftCard, 0);
        grid.Children.Add(leftCard);

        var rightCard = CreateCard(margin: new Thickness(6, 0, 0, 0));
        var rightStack = new StackPanel();
        rightStack.Children.Add(SectionHeading(Loc.T("GuardCenter.Section.HardwareTelemetry")));
        AddMetricRow(rightStack, Loc.T("GuardCenter.Metric.GpuTelemetry"), _gpuText);
        AddMetricRow(rightStack, Loc.T("GuardCenter.Metric.RamCommit"), _ramCommitText);
        AddMetricRow(rightStack, Loc.T("GuardCenter.Metric.PerfMetrics"), _perfMetricsText);
        AddMetricRow(rightStack, Loc.T("GuardCenter.Metric.RollbackStatus"), _rollbackText);

        var progressRow = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        var progLabel = new TextBlock { Text = Loc.T("GuardCenter.Metric.LoadingProgress"), Foreground = BrushOf("#8B949E"), FontSize = 12, Width = 132 };
        DockPanel.SetDock(progLabel, Dock.Left);
        _loadingPctText.Foreground = BrushOf("#7EE787");
        _loadingPctText.FontWeight = FontWeights.Bold;
        _loadingPctText.FontSize = 12;
        _loadingPctText.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_loadingPctText, Dock.Right);
        _loadingBar.Minimum = 0;
        _loadingBar.Maximum = 100;
        _loadingBar.Height = 10;
        _loadingBar.Foreground = BrushOf("#3FB950");
        _loadingBar.Background = BrushOf("#21262D");
        progressRow.Children.Add(progLabel);
        progressRow.Children.Add(_loadingPctText);
        progressRow.Children.Add(_loadingBar);
        rightStack.Children.Add(progressRow);

        rightCard.Child = rightStack;
        Grid.SetColumn(rightCard, 1);
        grid.Children.Add(rightCard);
        return grid;
    }

    private Border BuildLifecycleControlCard()
    {
        var card = CreateCard();
        var stack = new StackPanel();
        stack.Children.Add(SectionHeading(Loc.T("GuardCenter.Section.LifecycleControl")));

        var stagesPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        for (int i = 0; i < GuardControlBridgeService.LifecycleStages.Length; i++)
        {
            var stageKey = $"GuardCenter.Stage.{i + 1}";
            var stageName = Loc.T(stageKey);
            var pill = new Border
            {
                Background = BrushOf("#1C2430"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Child = new TextBlock
                {
                    Text = $"{i + 1}. {stageName}",
                    FontSize = 11.5,
                    Foreground = BrushOf("#8B949E")
                }
            };
            _stagePills[i] = pill;
            stagesPanel.Children.Add(pill);
        }
        stack.Children.Add(stagesPanel);

        _failureReasonText.FontSize = 12;
        _failureReasonText.Foreground = BrushOf("#FFA657");
        _failureReasonText.TextWrapping = TextWrapping.Wrap;
        _failureReasonText.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(_failureReasonText);

        var buttons = new WrapPanel();
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.Refresh"), "#21262D", "#E6EDF3", () => _ = _actions.RefreshAsync(), isMutating: false));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.Pause"), "#2D333B", "#E6EDF3", () => _ = _actions.PauseAsync(), isMutating: true));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.Resume"), "#1F6FEB", "#FFFFFF", () => _ = _actions.ResumeAsync(), isMutating: true));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.Unload"), "#492323", "#FFA198", () => _ = _actions.UnloadAsync(), isMutating: true));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.CancelWait"), "#2D333B", "#D2A8FF", () => _actions.CancelWait(), isMutating: false));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.Rollback"), "#3A2C0F", "#F2CC60", () => _ = _actions.RollbackAsync(), isMutating: true));
        _unlockFaultButton = CreateActionButton(Loc.T("GuardCenter.Action.UnlockFault"), "#421519", "#FF7B72", () => _ = _actions.UnlockFaultAsync(), isMutating: false);
        _unlockFaultButton.IsEnabled = false;
        buttons.Children.Add(_unlockFaultButton);
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.NavigateModels"), "#16263A", "#79C0FF", _actions.NavigateToModels, isMutating: false));
        buttons.Children.Add(CreateActionButton(Loc.T("GuardCenter.Action.NavigateRuntimes"), "#16263A", "#79C0FF", _actions.NavigateToRuntimes, isMutating: false));

        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }

    private static Border CreateCard(Thickness? margin = null) => new()
    {
        Background = BrushOf("#161D29"),
        BorderBrush = BrushOf("#263244"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
        Margin = margin ?? new Thickness(0, 0, 0, 10)
    };

    private static TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = BrushOf("#79C0FF"),
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static void AddMetricRow(StackPanel parent, string label, TextBlock valueBlock)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock { Text = label, Foreground = BrushOf("#8B949E"), FontSize = 12, Width = 132 };
        DockPanel.SetDock(lbl, Dock.Left);
        valueBlock.Foreground = BrushOf("#E6EDF3");
        valueBlock.FontSize = 12;
        valueBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(lbl);
        row.Children.Add(valueBlock);
        parent.Children.Add(row);
    }

    private WpfButton CreateActionButton(string text, string bgHex, string fgHex, Action onClick, bool isMutating)
    {
        var btn = new WpfButton
        {
            Content = text,
            Background = BrushOf(bgHex),
            Foreground = BrushOf(fgHex),
            BorderBrush = BrushOf("#30363D"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 6),
            FontSize = 12
        };
        btn.Click += (_, _) => onClick();
        if (isMutating) _mutatingButtons.Add(btn);
        return btn;
    }

    private static SolidColorBrush BrushOf(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
