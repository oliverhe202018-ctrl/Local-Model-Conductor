using System.Text.Json;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service;

// Independent foreground/background process host for LocalLLMManager.Guard (Phase 4A).
// Explicitly does NOT register any Windows Service or startup scheduled task.
var stateDir = GetArg(args, "--state-dir") ?? Path.Combine(AppContext.BaseDirectory, "guard_state");
var pipeName = GetArg(args, "--pipe") ?? "LocalLLMManager.Guard.Control.Isolated";
var listenPort = int.TryParse(GetArg(args, "--listen-port"), out var lp) ? lp : 19250;
var internalUrl = GetArg(args, "--internal-url") ?? "http://127.0.0.1:19259";
var profilesPath = GetArg(args, "--profiles-json");
var requireAgentKey = !string.Equals(GetArg(args, "--require-agent-key"), "false", StringComparison.OrdinalIgnoreCase);

var profiles = new Dictionary<string, GuardProfileSpec>(StringComparer.OrdinalIgnoreCase);
if (!string.IsNullOrWhiteSpace(profilesPath) && File.Exists(profilesPath))
{
    var list = JsonSerializer.Deserialize<List<GuardProfileSpec>>(
        File.ReadAllText(profilesPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    foreach (var p in list)
        profiles[p.ProfileName] = p;
}

await using var host = GuardHostFactory.Build(new GuardHostConfig
{
    StateDirectory = stateDir,
    PipeName = pipeName,
    AgentFacadeListenPort = listenPort,
    InternalLlamaSwapBaseUrl = internalUrl,
    RequireAgentInferenceApiKey = requireAgentKey,
    Profiles = profiles
});

Console.WriteLine($"[LocalLLMManager.Guard] Starting independent Guard process PID={Environment.ProcessId}");
Console.WriteLine($"[LocalLLMManager.Guard] Agent Inference Facade: http://127.0.0.1:{listenPort}/v1 (Management APIs blocked)");
Console.WriteLine($"[LocalLLMManager.Guard] ACL Control Pipe: \\\\.\\pipe\\{pipeName}");
Console.WriteLine($"[LocalLLMManager.Guard] Credentials sealed via DPAPI at: {host.CredentialStore.CredentialFilePath}");

await host.StartAsync();
await host.App.WaitForShutdownAsync();

static string? GetArg(string[] argv, string name)
{
    for (int i = 0; i < argv.Length - 1; i++)
    {
        if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
            return argv[i + 1];
    }
    return null;
}
