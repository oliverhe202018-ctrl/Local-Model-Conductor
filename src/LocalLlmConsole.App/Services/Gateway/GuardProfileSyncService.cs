using System.Security.Cryptography;
using System.Text;
using LocalLLMManager.Guard.Contracts;

namespace LocalLlmConsole.Services;

public sealed record LaunchProfileValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    double EstimatedVramMiB,
    double EstimatedRamMiB,
    double EstimatedCommitMiB);

public sealed record GuardYamlSyncArtifact(
    string GeneratedYaml,
    string ConfigSha256,
    IReadOnlyList<GuardProfileSpec> ProfileSpecs,
    DateTimeOffset GeneratedAtUtc);

/// <summary>
/// Phase 4B-1 / 4B-3 Single-Direction SQLite -> Guard -> llama-swap Config Compiler & Parameter Validator.
/// Launch Profiles in SQLite remain pure asset definitions and NEVER launch llama-server directly.
/// </summary>
public static class GuardProfileSyncService
{
    public static LaunchProfileValidationResult ValidateAndEstimate(
        ModelRecord model,
        RuntimeRecord? runtime,
        ModelLaunchSettings settings)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings.ContextSize < 512 || settings.ContextSize > 1_048_576)
            errors.Add($"ctx-size ({settings.ContextSize}) 必须介于 512 与 1,048,576 之间。");
        if (settings.ParallelSlots < 1 || settings.ParallelSlots > 32)
            errors.Add($"Parallel Slots ({settings.ParallelSlots}) 必须介于 1 与 32 之间。");
        if (settings.Port < 1024 || settings.Port > 65535)
            errors.Add($"上游端口 ({settings.Port}) 不在合法范围 1024–65535 内。");
        if (runtime is null || string.IsNullOrWhiteSpace(runtime.ExecutablePath))
            errors.Add("必须先为该模型绑定一个已注册的 llama.cpp Runtime。");

        var fileBytes = File.Exists(model.ModelPath) ? new FileInfo(model.ModelPath).Length : 0L;
        var ggufMiB = fileBytes > 0 ? fileBytes / (1024.0 * 1024.0) : 14_000.0;

        // MoE-aware estimation: inspect --n-cpu-moe in CustomParameters so 35B MoE Qwen/Cyber-Tiel is not misjudged!
        var hasCpuMoe = (settings.CustomParameters ?? "").Contains("cpu-moe", StringComparison.OrdinalIgnoreCase);
        var vramRatio = settings.GpuLayers == 0 ? 0.0 : (hasCpuMoe ? 0.56 : 0.92);
        var kvMiB = Math.Round((settings.ContextSize / 32768.0) * 1400.0 * Math.Max(1, settings.ParallelSlots), 0);
        var estVram = Math.Round(ggufMiB * vramRatio + kvMiB, 0);
        var estRam = Math.Round(ggufMiB * (1.0 - vramRatio) + 2048.0, 0);
        var estCommit = Math.Round(estRam + kvMiB + 3072.0, 0);

        if (estVram > 15_200)
            warnings.Add($"预计峰值显存 {estVram:F0} MiB 接近 16GB 单卡上限，建议开启 --n-cpu-moe 或降低 ctx-size。");

        return new LaunchProfileValidationResult(errors.Count == 0, errors, warnings, estVram, estRam, estCommit);
    }

    public static string ComputeFileSha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static GuardYamlSyncArtifact ExportCandidateSyncArtifact(
        IReadOnlyList<ModelRecord> models,
        IReadOnlyList<RuntimeRecord> runtimes,
        IReadOnlyList<NamedModelLaunchProfile> profiles,
        AppSettings appSettings,
        string logicalModelId = GuardBackendOptions.DefaultLogicalModelId)
    {
        var runtimeById = runtimes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var modelById = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var specs = new List<GuardProfileSpec>();
        var sb = new StringBuilder();
        sb.AppendLine("# Managed one-way candidate export from llama.cpp Windows Manager SQLite -> LocalLLMManager.Guard");
        sb.AppendLine("healthCheckTimeout: 120");
        sb.AppendLine("sendLoadingState: true");
        sb.AppendLine("globalConcurrencyLimit: 0");
        sb.AppendLine();
        sb.AppendLine("profiles:");

        var memberIds = new List<string>();
        foreach (var profile in profiles)
        {
            if (!modelById.TryGetValue(profile.ModelId, out var model))
                continue;
            runtimeById.TryGetValue(profile.Settings.RuntimeId, out var runtime);
            var safeProfileKey = ModelGatewayRouteId.SafeSegment($"{model.Name}-{profile.Name}");
            var safeModelKey = ModelGatewayRouteId.SafeSegment(model.Id);
            var validation = ValidateAndEstimate(model, runtime, profile.Settings);
            var runtimeSha = runtime is not null && File.Exists(runtime.ExecutablePath) ? ComputeFileSha256(runtime.ExecutablePath) : null;

            sb.AppendLine($"  {safeProfileKey}:");
            sb.AppendLine($"    description: \"{EscapeYaml(model.Name)} ({EscapeYaml(profile.Name)})\"");
            sb.AppendLine("    pins:");
            sb.AppendLine($"      {logicalModelId}: {safeModelKey}");

            specs.Add(new GuardProfileSpec
            {
                ProfileName = safeProfileKey,
                ModelId = safeModelKey,
                UpstreamPort = profile.Settings.Port,
                RuntimeExecutablePath = runtime?.ExecutablePath ?? string.Empty,
                ExpectedRuntimeSha256 = runtimeSha,
                GgufPath = model.ModelPath,
                ContextLength = profile.Settings.ContextSize,
                RequiredVramMiB = validation.EstimatedVramMiB,
                RequiredRamMiB = validation.EstimatedRamMiB,
                RequiredCommitMiB = validation.EstimatedCommitMiB
            });
            memberIds.Add(safeModelKey);
        }

        sb.AppendLine();
        sb.AppendLine("models:");
        foreach (var spec in specs.DistinctBy(s => s.ModelId, StringComparer.OrdinalIgnoreCase))
        {
            var exe = spec.RuntimeExecutablePath.Replace("\\", "/");
            var gguf = (spec.GgufPath ?? "").Replace("\\", "/");
            sb.AppendLine($"  {spec.ModelId}:");
            sb.AppendLine($"    proxy: \"http://127.0.0.1:{spec.UpstreamPort}\"");
            sb.AppendLine($"    cmd: '\"{exe}\" -m \"{gguf}\" --host 127.0.0.1 --port {spec.UpstreamPort} -c {spec.ContextLength}'");
            sb.AppendLine("    checkEndpoint: \"/health\"");
        }

        sb.AppendLine();
        sb.AppendLine("groups:");
        sb.AppendLine("  gpu_exclusive:");
        sb.AppendLine("    swap: true");
        sb.AppendLine("    exclusive: true");
        sb.AppendLine("    members:");
        foreach (var id in memberIds.Distinct(StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"      - {id}");

        var yaml = sb.ToString();
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
        return new GuardYamlSyncArtifact(yaml, sha, specs, DateTimeOffset.UtcNow);
    }

    private static string EscapeYaml(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
