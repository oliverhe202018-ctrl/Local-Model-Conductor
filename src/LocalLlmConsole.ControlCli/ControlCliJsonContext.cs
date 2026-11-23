using System.Text.Json;
using System.Text.Json.Serialization;
using LocalLLMManager.Guard.Contracts;

namespace LocalLlmConsole.ControlCli;

internal sealed record ControlCliError(bool Ok, string Error);

internal sealed record ControlCliGatewayHealth(
    bool Ok,
    bool GatewayHealthy,
    string OperationalState,
    string? ActiveProfile,
    bool ExternalGpuConflict);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiscoveryDocument))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ControlCliError))]
[JsonSerializable(typeof(ControlCliGatewayHealth))]
[JsonSerializable(typeof(GuardCommandResponse))]
internal sealed partial class ControlCliJsonContext : JsonSerializerContext
{
    public static ControlCliJsonContext Indented { get; } = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });
}
