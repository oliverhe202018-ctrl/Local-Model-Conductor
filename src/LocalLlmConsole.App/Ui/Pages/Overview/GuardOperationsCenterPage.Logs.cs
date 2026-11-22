using System.Text;
using System.Windows;
using System.Windows.Controls;
using LocalLlmConsole.Services;

namespace LocalLlmConsole;

public sealed partial class GuardOperationsCenterPage
{
    private Border BuildLiveTerminalSection()
    {
        var card = CreateCard(margin: new Thickness(0));
        var stack = new StackPanel();
        stack.Children.Add(SectionHeading("实时终端与会话日志（自动脱敏 API Key / 严禁混入 Codex 与 Hermes 日志）"));

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };

        _logSearchBox.Width = 200;
        _logSearchBox.Height = 26;
        _logSearchBox.Background = BrushOf("#0B0F15");
        _logSearchBox.Foreground = BrushOf("#E6EDF3");
        _logSearchBox.BorderBrush = BrushOf("#30363D");
        _logSearchBox.Margin = new Thickness(0, 0, 8, 4);
        _logSearchBox.ToolTip = "搜索日志关键字";
        _logSearchBox.TextChanged += (_, _) => RefreshLogView();
        toolbar.Children.Add(_logSearchBox);

        _pauseScrollCheck.Content = "暂停滚动";
        _pauseScrollCheck.Foreground = BrushOf("#C9D1D9");
        _pauseScrollCheck.Margin = new Thickness(0, 4, 10, 4);
        _pauseScrollCheck.Checked += (_, _) => _logService.IsPaused = true;
        _pauseScrollCheck.Unchecked += (_, _) => { _logService.IsPaused = false; RefreshLogView(); };
        toolbar.Children.Add(_pauseScrollCheck);

        _errorOnlyCheck.Content = "仅错误";
        _errorOnlyCheck.Foreground = BrushOf("#FFA198");
        _errorOnlyCheck.Margin = new Thickness(0, 4, 10, 4);
        _errorOnlyCheck.Checked += (_, _) => RefreshLogView();
        _errorOnlyCheck.Unchecked += (_, _) => RefreshLogView();
        toolbar.Children.Add(_errorOnlyCheck);

        _cudaOomCheck.Content = "仅 CUDA/OOM";
        _cudaOomCheck.Foreground = BrushOf("#F2CC60");
        _cudaOomCheck.Margin = new Thickness(0, 4, 10, 4);
        _cudaOomCheck.Checked += (_, _) => RefreshLogView();
        _cudaOomCheck.Unchecked += (_, _) => RefreshLogView();
        toolbar.Children.Add(_cudaOomCheck);

        _modelFilterCombo.Width = 135;
        _modelFilterCombo.Margin = new Thickness(0, 0, 8, 4);
        _modelFilterCombo.Items.Add("全部模型");
        _modelFilterCombo.Items.Add("cyber-tiel-mock");
        _modelFilterCombo.Items.Add("qwen-mock");
        _modelFilterCombo.Items.Add("inkling2-mock");
        _modelFilterCombo.SelectedIndex = 0;
        _modelFilterCombo.SelectionChanged += (_, _) => RefreshLogView();
        toolbar.Children.Add(_modelFilterCombo);

        _sessionFilterCombo.Width = 135;
        _sessionFilterCombo.Margin = new Thickness(0, 0, 8, 4);
        _sessionFilterCombo.Items.Add("全部 Session");
        _sessionFilterCombo.SelectedIndex = 0;
        _sessionFilterCombo.SelectionChanged += (_, _) => RefreshLogView();
        toolbar.Children.Add(_sessionFilterCombo);

        toolbar.Children.Add(CreateActionButton("导出脱敏日志", "#16263A", "#79C0FF", () =>
        {
            var exportPath = Path.Combine(
                Path.GetTempPath(),
                $"guard-exported-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
            _ = _actions.ExportLogsAsync(exportPath);
        }, isMutating: false));

        stack.Children.Add(toolbar);

        _logBox.Height = 200;
        _logBox.IsReadOnly = true;
        _logBox.TextWrapping = TextWrapping.NoWrap;
        _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logBox.Background = BrushOf("#0B0F15");
        _logBox.Foreground = BrushOf("#A5D6FF");
        _logBox.BorderBrush = BrushOf("#21262D");
        _logBox.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _logBox.FontSize = 11.5;
        _logBox.Padding = new Thickness(8);
        stack.Children.Add(_logBox);

        card.Child = stack;
        return card;
    }

    public GuardLogFilter CurrentLogFilter()
    {
        var model = _modelFilterCombo.SelectedIndex > 0 ? _modelFilterCombo.SelectedItem?.ToString() : null;
        var session = _sessionFilterCombo.SelectedIndex > 0 ? _sessionFilterCombo.SelectedItem?.ToString() : null;
        return new GuardLogFilter(
            SearchText: _logSearchBox.Text,
            ErrorsOnly: _errorOnlyCheck.IsChecked == true,
            CudaOrOomOnly: _cudaOomCheck.IsChecked == true,
            ModelId: model,
            SessionId: session);
    }

    public void RefreshLogView()
    {
        var lines = _logService.Query(CurrentLogFilter());
        var sb = new StringBuilder();
        foreach (var l in lines.TakeLast(300))
            sb.AppendLine($"[{l.TimestampUtc.ToLocalTime():HH:mm:ss}] [{l.Level}] [{l.ModelId}] {l.Text}");

        _logBox.Text = sb.ToString();
        if (!_logService.IsPaused)
            _logBox.ScrollToEnd();
    }
}
