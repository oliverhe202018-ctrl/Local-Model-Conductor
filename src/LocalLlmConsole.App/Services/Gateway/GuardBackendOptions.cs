namespace LocalLlmConsole.Services;

/// <summary>
/// Runtime configuration for Phase 4B Guard & Fixed-Gateway integration.
/// Defaults to fixed production contract (http://127.0.0.1:19000/v1, local-coder),
/// while allowing isolated Phase 4B CLI/env overrides (--guard-pipe, --gateway-url, --workspace, --capture-screenshots).
/// </summary>
public sealed record GuardBackendOptions(
    string GuardPipeName,
    string FixedGatewayBaseUrl,
    string FixedLogicalModelId,
    string? WorkspaceOverride,
    string? ScreenshotOutputDirectory,
    string? UiSwitchWorkflowOutputDirectory = null)
{
    public const string DefaultPipeName = "LocalLLMManager.Guard.Control.v1";
    public const string DefaultGatewayBaseUrl = "http://127.0.0.1:19000/v1";
    public const string DefaultLogicalModelId = "local-coder";

    public static GuardBackendOptions Current { get; private set; } = FromEnvironmentAndArgs([]);

    public static GuardBackendOptions Configure(string[] args)
    {
        Current = FromEnvironmentAndArgs(args);
        return Current;
    }

    public static GuardBackendOptions FromEnvironmentAndArgs(string[] args)
    {
        var pipe = ReadArg(args, "--guard-pipe")
            ?? Environment.GetEnvironmentVariable("LLWM_GUARD_PIPE")
            ?? DefaultPipeName;
        var gatewayUrl = ReadArg(args, "--gateway-url")
            ?? Environment.GetEnvironmentVariable("LLWM_GATEWAY_URL")
            ?? DefaultGatewayBaseUrl;
        var logicalModel = ReadArg(args, "--logical-model")
            ?? Environment.GetEnvironmentVariable("LLWM_LOGICAL_MODEL")
            ?? DefaultLogicalModelId;
        var workspace = ReadArg(args, "--workspace")
            ?? Environment.GetEnvironmentVariable("LLWM_WORKSPACE");
        var screenshots = ReadArg(args, "--capture-screenshots");
        var uiSwitchWorkflow = ReadArg(args, "--execute-ui-switch-workflow");

        return new GuardBackendOptions(
            pipe.Trim(),
            gatewayUrl.Trim(),
            logicalModel.Trim(),
            string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim(),
            string.IsNullOrWhiteSpace(screenshots) ? null : screenshots.Trim(),
            string.IsNullOrWhiteSpace(uiSwitchWorkflow) ? null : uiSwitchWorkflow.Trim());
    }

    private static string? ReadArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
