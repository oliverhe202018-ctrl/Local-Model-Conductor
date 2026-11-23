using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Control;

/// <summary>
/// Windows ACL-protected Named Pipe control server for LocalLLMManager.Guard.
///
/// Both WPF Desktop UI and Guard CLI connect to this single IPC channel.
/// Neither UI nor CLI holds llama-swap's internal admin credential.
///
/// Explicit PipeSecurity ACL:
/// - Allow FullControl: Current Windows User SID + Builtin Administrators + LocalSystem
/// - Deny FullControl: NetworkSid (remote SMB pipe access blocked) + AnonymousSid
/// </summary>
public sealed class NamedPipeControlServer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly GuardControlCoordinator _coordinator;
    private readonly Func<string?[]> _secretsProvider;

    public NamedPipeControlServer(
        string pipeName,
        GuardControlCoordinator coordinator,
        Func<string?[]>? secretsProvider = null)
    {
        _pipeName = pipeName;
        _coordinator = coordinator;
        _secretsProvider = secretsProvider ?? (() => []);
    }

    public string PipeName => _pipeName;

    public static PipeSecurity CreateRestrictedPipeSecurity()
    {
        var ps = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
            ps.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return ps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeSecurity = CreateRestrictedPipeSecurity();

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName: _pipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous,
                    inBufferSize: 16384,
                    outBufferSize: 65536,
                    pipeSecurity: pipeSecurity);

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var connected = server;
                server = null; // Ownership transferred to background handler
                _ = Task.Run(() => HandleClientAsync(connected, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var request = JsonSerializer.Deserialize<GuardCommandRequest>(line, JsonOptions);
                GuardCommandResponse response;
                if (request is null)
                {
                    response = new GuardCommandResponse
                    {
                        Accepted = false,
                        Succeeded = false,
                        Code = "INVALID_IPC_PAYLOAD",
                        Message = "Malformed GuardCommandRequest JSON."
                    };
                }
                else
                {
                    response = await _coordinator.ExecuteCommandAsync(request, ct).ConfigureAwait(false);
                }

                var respJson = LogRedactor.Redact(JsonSerializer.Serialize(response, JsonOptions), _secretsProvider());
                var respBytes = Encoding.UTF8.GetBytes(respJson + "\n");
                await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Client disconnected or invalid stream
            }
        }
    }
}
