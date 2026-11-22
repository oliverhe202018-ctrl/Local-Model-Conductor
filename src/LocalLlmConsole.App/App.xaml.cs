using System.Windows;

namespace LocalLlmConsole;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\llama.cpp-console-single-instance";

    private readonly SingleInstanceApplicationService _singleInstance = new(SingleInstanceApplicationService.AcquireMutexLease);
    private readonly DialogService _dialogs = new(ThemedMessageBox.Show);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Phase 4B-1 P0: Irrevocably lock the desktop application into ExternalLlamaSwap mode.
        ExternalExecutionPolicy.LockExternalLlamaSwapForRuntime();
        var guardOptions = GuardBackendOptions.Configure(e.Args);
        if (!string.IsNullOrWhiteSpace(guardOptions.WorkspaceOverride))
            Environment.SetEnvironmentVariable(WorkspaceRootResolver.EnvironmentVariable, guardOptions.WorkspaceOverride);

        if (e.Args.Contains("--bootstrap-agent-sidecars-only", StringComparer.OrdinalIgnoreCase))
        {
            var sidecars = new AgentSidecarBootstrapService().InstallPackaged(
                Environment.ProcessPath ?? "",
                AppContext.BaseDirectory,
                verifyBundleContents: true);
            if (AgentSidecarBootstrapService.DescribeProblem(sidecars) is { } problem)
                Trace.TraceWarning(problem);
            Shutdown(AgentSidecarBootstrapService.VerificationExitCode(sidecars.Status));
            return;
        }

        var skipSingleInstance = !string.IsNullOrWhiteSpace(guardOptions.ScreenshotOutputDirectory)
            || e.Args.Contains("--isolated-instance", StringComparer.OrdinalIgnoreCase);

        if (!skipSingleInstance && !_singleInstance.TryAcquire(SingleInstanceMutexName))
        {
            _dialogs.Notify(null, "llama.cpp Windows Manager is already running.", "llama.cpp Windows Manager", MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        if (!string.IsNullOrWhiteSpace(guardOptions.ScreenshotOutputDirectory))
        {
            window.ShowInTaskbar = false;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await window.CaptureGuardWorkbenchScreenshotsAsync(guardOptions.ScreenshotOutputDirectory!);
                }
                finally
                {
                    Shutdown(0);
                }
            };
        }
        window.Show();
        if (!skipSingleInstance)
            _ = BootstrapPackagedSidecarsAsync();
    }

    private static async Task BootstrapPackagedSidecarsAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var result = await Task.Run(() => new AgentSidecarBootstrapService().InstallPackaged(
                Environment.ProcessPath ?? "",
                AppContext.BaseDirectory));
            if (AgentSidecarBootstrapService.DescribeProblem(result) is { } problem)
                Trace.TraceWarning(problem);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Agent control sidecar bootstrap failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance.Dispose();
        base.OnExit(e);
    }
}
