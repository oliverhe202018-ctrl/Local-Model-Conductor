using System.Text.Json;
using LocalLLMManager.Guard.Contracts;

namespace LocalLlmConsole.ControlCli;

/// <summary>
/// Phase 4B-5 Guard IPC dispatcher for llwmctl.
/// Routes ALL model-control mutations (switch, rollback, pause, resume, unlock) and Guard gateway queries
/// (gateway status, gateway health, logs audit) exclusively through Guard's Windows ACL Named Pipe.
/// llwmctl NEVER holds Guard's internal llama-swap admin key and NEVER calls llama-swap admin HTTP APIs directly.
/// </summary>
internal static class ControlCliGuardDispatcher
{
    private const string DefaultGuardPipe = "LocalLLMManager.Guard.Control.v1";

    internal static bool TryResolveGuardOperation(Arguments args, out string operationKey)
    {
        operationKey = "";
        if (args.Positionals.Count == 0)
            return false;

        var group = args.Positionals[0].ToLowerInvariant();
        var action = args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";

        if (group is "switch" or "rollback" or "pause" or "resume" or "unlock")
        {
            operationKey = group;
            return true;
        }

        if (group == "gateway" && action is "status" or "health")
        {
            operationKey = $"gateway.{action}";
            return true;
        }

        if (group is "logs" or "log" && action is "audit" or "guard")
        {
            operationKey = "logs.audit";
            return true;
        }

        return false;
    }

    internal static async Task<int> ExecuteAsync(Arguments args, string operationKey)
    {
        var pipeName = args.Value("guard-pipe")
            ?? Environment.GetEnvironmentVariable("LLWM_GUARD_PIPE")
            ?? DefaultGuardPipe;
        var idempotency = args.Value("idempotency") ?? Guid.NewGuid().ToString("N");
        var client = new GuardPipeClient(pipeName);

        GuardCommandResponse response;
        switch (operationKey)
        {
            case "gateway.status":
            case "gateway.health":
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.GET_STATUS,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}"
                });
                if (operationKey == "gateway.health")
                {
                    var healthy = response.Succeeded && response.Status?.GatewayHealthy == true;
                    var health = new ControlCliGatewayHealth(
                        healthy,
                        response.Status?.GatewayHealthy ?? false,
                        response.OperationalState.ToString(),
                        response.ActiveProfile,
                        response.Status?.ExternalGpuConflictActive ?? false);
                    Console.WriteLine(JsonSerializer.Serialize(health, ControlCliJsonContext.Indented.ControlCliGatewayHealth));
                    return healthy ? 0 : 1;
                }
                break;

            case "switch":
                var target = args.Value("target") ?? args.Positionals.ElementAtOrDefault(1);
                if (string.IsNullOrWhiteSpace(target))
                    throw new InvalidOperationException("Specify a target profile: llwmctl switch <profile> (or --target <profile>).");
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.SWITCH_PROFILE,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}",
                    TargetProfile = target.Trim()
                });
                break;

            case "rollback":
                var snap = await client.SendAsync(new GuardCommandRequest
                {
                    Command = GuardCommandType.GET_STATUS,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}"
                });
                var baseline = args.Value("target") ?? snap.LastKnownGoodProfile;
                if (string.IsNullOrWhiteSpace(baseline))
                    throw new InvalidOperationException("No LastKnownGoodProfile baseline available in Guard to roll back to.");
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.SWITCH_PROFILE,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}",
                    TargetProfile = baseline,
                    Reason = "llwmctl explicit rollback"
                });
                break;

            case "pause":
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.PAUSE_INFERENCE,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}"
                });
                break;

            case "resume":
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.RESUME_INFERENCE,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}"
                });
                break;

            case "unlock":
                response = await client.SendAsync(new GuardCommandRequest
                {
                    IdempotencyKey = idempotency,
                    Command = GuardCommandType.UNLOCK_FAULT,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}",
                    Reason = args.Value("reason") ?? "llwmctl operator unlock"
                });
                break;

            case "logs.audit":
                response = await client.SendAsync(new GuardCommandRequest
                {
                    Command = GuardCommandType.GET_AUDIT_TAIL,
                    CallerRole = GuardCallerRole.CliClient,
                    CallerId = $"llwmctl:{Environment.UserName}"
                });
                break;

            default:
                throw new InvalidOperationException($"Unsupported Guard CLI operation '{operationKey}'.");
        }

        Console.WriteLine(JsonSerializer.Serialize(response, ControlCliJsonContext.Indented.GuardCommandResponse));
        return response.Succeeded ? 0 : 1;
    }
}
