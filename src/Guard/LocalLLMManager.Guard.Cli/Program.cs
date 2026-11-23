using System.Text.Json;
using LocalLLMManager.Guard.Contracts;

namespace LocalLLMManager.Guard.Cli;

/// <summary>
/// Reference Guard CLI sharing the EXACT same ACL Named Pipe IPC entry point (GuardPipeClient) as WPF UI.
/// Never stores or accesses llama-swap's internal admin key.
/// </summary>
public static class GuardCliEntrypoint
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: LocalLLMManager.Guard.Cli <status|switch|pause|resume|unload|unlock|audit> [--target <profile>] [--pipe <name>] [--role CliClient|UiClient] [--idempotency <key>] [--reason <text>]");
            return 2;
        }

        var verb = args[0].Trim().ToLowerInvariant();
        var pipe = GetArg(args, "--pipe") ?? "LocalLLMManager.Guard.Control.Isolated";
        var target = GetArg(args, "--target");
        var idempotency = GetArg(args, "--idempotency") ?? Guid.NewGuid().ToString("N");
        var reason = GetArg(args, "--reason");
        var roleStr = GetArg(args, "--role") ?? "CliClient";
        var role = Enum.TryParse<GuardCallerRole>(roleStr, ignoreCase: true, out var parsedRole)
            ? parsedRole
            : GuardCallerRole.CliClient;

        var cmd = verb switch
        {
            "status" => GuardCommandType.GET_STATUS,
            "switch" => GuardCommandType.SWITCH_PROFILE,
            "pause" => GuardCommandType.PAUSE_INFERENCE,
            "resume" => GuardCommandType.RESUME_INFERENCE,
            "unload" => GuardCommandType.UNLOAD_ALL,
            "unlock" => GuardCommandType.UNLOCK_FAULT,
            "audit" => GuardCommandType.GET_AUDIT_TAIL,
            _ => (GuardCommandType?)null
        };

        if (cmd is null)
        {
            Console.Error.WriteLine($"Unknown command: {verb}");
            return 2;
        }

        var client = new GuardPipeClient(pipe);
        var resp = await client.SendAsync(new GuardCommandRequest
        {
            IdempotencyKey = idempotency,
            Command = cmd.Value,
            CallerRole = role,
            CallerId = $"{role}:{Environment.UserName}",
            TargetProfile = target,
            Reason = reason
        });

        Console.WriteLine(JsonSerializer.Serialize(resp, JsonOut));
        return resp.Succeeded ? 0 : 1;
    }

    private static string? GetArg(string[] argv, string name)
    {
        for (int i = 1; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
                return argv[i + 1];
        }
        return null;
    }
}
