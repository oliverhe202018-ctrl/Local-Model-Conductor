using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LocalLLMManager.Guard.Service.Security;

public sealed record GuardCredentialSet(
    string InternalLlamaSwapAdminKey,
    string AgentInferenceApiKey,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Manages separated credentials using Windows DPAPI (CurrentUser scope) and strict NTFS File ACLs.
///
/// SECURITY PRINCIPLE (P0):
/// 1. InternalLlamaSwapAdminKey ("guard-internal-...") is strictly private to Guard & its managed llama-swap process.
///    It is NEVER handed to Agents, WPF UI, CLI, or written to any log.
/// 2. AgentInferenceApiKey ("agent-inf-...") is strictly for /v1 inference calls through Guard's Inference Facade.
///    It is NEVER accepted on management endpoints.
///
/// HONEST THREAT-MODEL BOUNDARY:
/// Because DPAPI CurrentUser and same-user NTFS ACLs share the logged-in Windows User SID, a malicious process
/// running under the SAME Windows user account with full code execution could theoretically invoke CryptUnprotectData.
/// True OS-level compromise isolation against same-user code execution requires running Guard under a dedicated
/// Windows Service account (e.g., LocalService / dedicated virtual service account), documented in phase4a_security_boundary.md.
/// </summary>
public sealed class GuardCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalLLMManager.Guard.Phase4A.v1");

    public string CredentialFilePath { get; }
    public GuardCredentialSet Credentials { get; }

    public GuardCredentialStore(string stateDirectory, GuardCredentialSet? overrideCredentials = null)
    {
        Directory.CreateDirectory(stateDirectory);
        ApplyStrictDirectoryAcl(stateDirectory);
        CredentialFilePath = Path.Combine(stateDirectory, "guard-credentials.dpapi.bin");

        if (overrideCredentials is not null)
        {
            Credentials = ValidateDistinctKeys(overrideCredentials);
            SaveProtected(Credentials);
        }
        else if (File.Exists(CredentialFilePath))
        {
            Credentials = ValidateDistinctKeys(LoadProtected());
        }
        else
        {
            Credentials = new GuardCredentialSet(
                InternalLlamaSwapAdminKey: $"guard-internal-{GenerateHexToken(32)}",
                AgentInferenceApiKey: $"agent-inf-{GenerateHexToken(24)}",
                CreatedAtUtc: DateTimeOffset.UtcNow);
            SaveProtected(Credentials);
        }
    }

    public static bool ConstantTimeEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static GuardCredentialSet ValidateDistinctKeys(GuardCredentialSet set)
    {
        if (string.IsNullOrWhiteSpace(set.InternalLlamaSwapAdminKey) || string.IsNullOrWhiteSpace(set.AgentInferenceApiKey))
            throw new InvalidOperationException("Both InternalLlamaSwapAdminKey and AgentInferenceApiKey must be non-empty.");
        if (string.Equals(set.InternalLlamaSwapAdminKey, set.AgentInferenceApiKey, StringComparison.Ordinal))
            throw new InvalidOperationException("SECURITY VIOLATION: Inference API key must NEVER equal Guard internal admin key.");
        return set;
    }

    private static string GenerateHexToken(int byteCount)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private void SaveProtected(GuardCredentialSet creds)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(creds);
        var cipher = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var tmp = CredentialFilePath + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        ApplyStrictFileAcl(tmp);
        File.Move(tmp, CredentialFilePath, overwrite: true);
        ApplyStrictFileAcl(CredentialFilePath);
    }

    private GuardCredentialSet LoadProtected()
    {
        var cipher = File.ReadAllBytes(CredentialFilePath);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<GuardCredentialSet>(plain)
            ?? throw new InvalidDataException("Failed to deserialize protected Guard credentials.");
    }

    public static void ApplyStrictFileAcl(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            var security = fi.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                security.RemoveAccessRule(rule);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(security);
        }
        catch
        {
            // Non-fatal on filesystems without NTFS ACL support; security boundary doc notes NTFS requirement.
        }
    }

    public static void ApplyStrictDirectoryAcl(string dirPath)
    {
        try
        {
            var di = new DirectoryInfo(dirPath);
            var security = di.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                security.RemoveAccessRule(rule);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(security);
        }
        catch
        {
            // Ignore on non-NTFS volumes
        }
    }
}
