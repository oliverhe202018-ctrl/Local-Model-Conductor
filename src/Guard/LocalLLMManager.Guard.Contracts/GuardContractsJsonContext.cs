using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalLLMManager.Guard.Contracts;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuardCommandRequest))]
[JsonSerializable(typeof(GuardCommandResponse))]
public sealed partial class GuardContractsJsonContext : JsonSerializerContext
{
    public static GuardContractsJsonContext DefaultContext { get; } = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
}
