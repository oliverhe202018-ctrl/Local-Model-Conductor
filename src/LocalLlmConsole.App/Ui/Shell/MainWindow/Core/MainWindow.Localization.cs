using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls;

namespace LocalLlmConsole;

public partial class MainWindow
{
    private void PopulateLanguageSelector()
    {
        LanguageCombo.ItemsSource = Localization.Loc.AvailableLanguages()
            .Select(code => new LanguageItem(code, Localization.Loc.LanguageDisplayName(code)))
            .ToList();
        LanguageCombo.DisplayMemberPath = "Name";
        LanguageCombo.SelectedValuePath = "Code";
        LanguageCombo.SelectedValue = Localization.Loc.NormalizeLanguageCode(_settings.UiCulture);
    }

    private sealed record LanguageItem(string Code, string Name) { public override string ToString() => Name; }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedValue is not string rawLang) return;
        var normalized = Localization.Loc.NormalizeLanguageCode(rawLang);
        if (normalized == Localization.Loc.CurrentLanguage && rawLang == _settings.UiCulture) return;

        Localization.Loc.LoadLanguage(rawLang);
        _settings = _settings with { UiCulture = rawLang };
        ApplyLocalizedXamlStrings();
        RefreshCurrentPage();
        if (_stateStore is null) return;
        try { await _stateStore.SaveAppSettingsAsync(_settings); }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not persist language selection: {ex}");
            SetStatus(Localization.Loc.T("Status.LanguageSaveFailed", ex.Message));
        }
    }

    private void ApplyLocalizedXamlStrings()
    {
        var culture = Localization.Loc.FormatCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        FlowDirection = Localization.Loc.IsRightToLeft(Localization.Loc.CurrentLanguage)
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
        ApplyStaticButtonToolTips();
        System.Windows.Automation.AutomationProperties.SetName(LanguageCombo, Localization.Loc.T("Status.Language"));
        System.Windows.Automation.AutomationProperties.SetHelpText(LanguageCombo, Localization.Loc.T("Status.Language"));
        System.Windows.Automation.AutomationProperties.SetName(AppStatusText, Localization.Loc.T("Status.CurrentActionLabel"));

        OverviewNavButton.Content = Localization.Loc.T("Nav.Overview");
        ModelsNavButton.Content = Localization.Loc.T("Nav.Models");
        RuntimesNavButton.Content = Localization.Loc.T("Nav.Runtimes");
        BenchmarksNavButton.Content = Localization.Loc.T("Nav.Benchmarks");
        SettingsNavButton.Content = Localization.Loc.T("Nav.Settings");
        LifetimeNavButton.Content = Localization.Loc.T("Nav.Lifetime");
        LogsNavButton.Content = Localization.Loc.T("Nav.Logs");
        ToolsNavLabel.Text = Localization.Loc.T("Nav.Tools");
        WindowsNavButton.Content = Localization.Loc.T("Nav.Windows");
        WslLinuxNavButton.Content = Localization.Loc.T("Nav.WslLinux");
        UpdatesNavButton.Content = Localization.Loc.T("Nav.CheckForUpdates");
        HelpNavButton.Content = Localization.Loc.T("Nav.Help");

        CurrentStatusLabel.Text = Localization.Loc.T("Status.CurrentActionLabel");
        AppTitleText.Text = Localization.Loc.T("App.Title");
        Title = $"{Localization.Loc.T("App.Title")} {AppVersionText.Text}";

        _trayProfileMenu?.Close();
    }
}
