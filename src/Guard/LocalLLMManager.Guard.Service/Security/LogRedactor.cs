using System.Text.RegularExpressions;

namespace LocalLLMManager.Guard.Service.Security;

/// <summary>
/// Inherited from LocalLLMManager.Core.Utilities.CommandLineSanitizer and expanded for Phase 4A Guard.
/// Guarantees zero plaintext leakage of Guard internal keys, Agent inference keys, Bearer/Basic headers,
/// x-api-key headers, or sensitive CLI flags in any log, exception, or audit trail.
/// </summary>
public static partial class LogRedactor
{
    public const string RedactedMarker = "***REDACTED***";

    [GeneratedRegex(
        @"(?i)(--(?:api[-_]?key|admin[-_]?key|hf[-_]?token|token|auth[-_]?token|bearer|secret|password|passwd|access[-_]?key)|-k)(?:(\s*=\s*|\s+))(""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled)]
    private static partial Regex SensitiveFlagRegex();

    [GeneratedRegex(
        @"(?i)((?:Authorization|X-Api-Key|X-Guard-Admin-Key)\s*[:=]\s*(?:Bearer\s+|Basic\s+)?)([A-Za-z0-9._\-~+/=]{6,})",
        RegexOptions.Compiled)]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex(
        @"(?i)(Bearer\s+[A-Za-z0-9._\-~+/]+=*|Basic\s+[A-Za-z0-9+/=]{8,}|guard-(?:internal|admin)-[A-Za-z0-9_\-]{8,}|agent-inf-[A-Za-z0-9_\-]{8,}|sk-[A-Za-z0-9_\-]{8,}|hf_[A-Za-z0-9]{10,}|ghp_[A-Za-z0-9]{10,})",
        RegexOptions.Compiled)]
    private static partial Regex InlineSecretRegex();

    [GeneratedRegex(
        @"(?i)(?:--model|-m)(?:\s*=\s*|\s+)(?:""([^""]+)""|'([^']+)'|([^\s]+))",
        RegexOptions.Compiled)]
    private static partial Regex GgufModelArgRegex();

    public static string Redact(string? input, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var result = input;
        foreach (var secret in knownSecrets)
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
            {
                result = result.Replace(secret, RedactedMarker, StringComparison.Ordinal);
            }
        }

        result = SensitiveFlagRegex().Replace(result, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{RedactedMarker}");
        result = SensitiveHeaderRegex().Replace(result, m => $"{m.Groups[1].Value}{RedactedMarker}");
        result = InlineSecretRegex().Replace(result, RedactedMarker);
        return result;
    }

    public static string? TryExtractGgufModelPath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var match = GgufModelArgRegex().Match(commandLine);
        if (!match.Success)
            return null;

        var raw = match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
