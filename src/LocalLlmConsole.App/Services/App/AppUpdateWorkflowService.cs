namespace LocalLlmConsole.Services;

public sealed record AppUpdateCheckWorkflowResult(
    AppUpdateInfo Update,
    string StatusMessage,
    bool ShouldPromptInstall,
    bool ShouldShowNoUpdateDialog,
    string DialogTitle,
    string DialogMessage);

public sealed class AppUpdateWorkflowService
{
    private readonly AppUpdateService _updates;
    private readonly string _workspaceRoot;

    public AppUpdateWorkflowService(AppUpdateService updates, string workspaceRoot)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot))
            : workspaceRoot;
    }

    public async Task<AppUpdateCheckWorkflowResult> CheckLatestAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var update = await _updates.CheckLatestAsync(linked.Token);
        return DescribeCheckResult(update, manual);
    }

    public async Task<string> StageAndStartInstallAsync(
        AppUpdateInfo update,
        string? currentExecutablePath,
        int currentProcessId,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var plan = await _updates.StageInstallAsync(update, _workspaceRoot, currentExecutablePath, linked.Token);
        await _updates.StartInstallerAsync(plan, currentProcessId, linked.Token);
        return "Update staged. Closing to install...";
    }

    public async Task<InstalledUpdateNotice?> TryConsumeInstalledNoticeAsync(CancellationToken cancellationToken = default)
        => await AppUpdateService.TryConsumeInstalledNoticeAsync(_workspaceRoot, cancellationToken);

    public static (string Title, string Message, bool Failed) DescribeInstalledNotice(InstalledUpdateNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (string.IsNullOrWhiteSpace(notice.FailureMessage))
        {
            return ("Update installed",
                $"Updated to {notice.Version}.\n\n{notice.ReleaseName}\n\n{DisplayFormatService.TrimForDisplay(notice.ReleaseNotes, 2600)}",
                false);
        }

        var target = string.IsNullOrWhiteSpace(notice.Version) ? "The update" : $"The update to {notice.Version}";
        return ("Update not installed",
            $"{target} could not be installed. The previous version was restored and restarted.\n\n" +
            $"{notice.FailureMessage}\n\nClose programs that use the Manager files, such as llwmctl.exe, and try the update again.",
            true);
    }

    public static AppUpdateCheckWorkflowResult DescribeCheckResult(AppUpdateInfo update, bool manual)
    {
        if (update.IsAvailable)
        {
            var isInstallerOnly = !update.CanAutoInstall;
            return new AppUpdateCheckWorkflowResult(
                update,
                isInstallerOnly
                    ? $"New version available ({update.LatestVersion}). Manual installer required."
                    : $"Update available: {update.LatestVersion}.",
                ShouldPromptInstall: manual && !isInstallerOnly,
                ShouldShowNoUpdateDialog: manual && isInstallerOnly,
                isInstallerOnly ? "Official Release Available" : "Install update",
                isInstallerOnly
                    ? $"Version {update.LatestVersion} is available on GitHub as an installer package.\n\nPlease visit the official release page to download and upgrade.\n\n{update.HtmlUrl}"
                    : $"Update {update.CurrentVersion} -> {update.LatestVersion} is available.\n\n{update.ReleaseName}\n\n" +
                      (update.AuthenticityVerified ? "" : "This release is unsigned. Its SHA-256 checksum will be checked before installation.\n\n") +
                      "Install it now?");
        }

        var isNoRelease = update.ReleaseName.Contains("No official release", StringComparison.OrdinalIgnoreCase)
            || update.ReleaseName.Contains("No GitHub release feed", StringComparison.OrdinalIgnoreCase);

        return new AppUpdateCheckWorkflowResult(
            update,
            isNoRelease ? "No official release has been published yet." : "No app updates available.",
            ShouldPromptInstall: false,
            ShouldShowNoUpdateDialog: manual,
            "Check for updates",
            isNoRelease
                ? $"No official release has been published yet.\n\nCurrent version: {update.CurrentVersion}"
                : $"No updates are available.\n\nCurrent version: {update.CurrentVersion}");
    }
}
