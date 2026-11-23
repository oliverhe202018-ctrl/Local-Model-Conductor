using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace LocalLLMManager.Guard.Contracts;

/// <summary>
/// Single shared Named Pipe IPC client used by both Desktop UI (WPF) and CLI.
/// Neither UI nor CLI ever receives or stores llama-swap's internal admin credential;
/// all management intents are serialized through LocalLLMManager.Guard.
/// </summary>
public sealed class GuardPipeClient
{
    private readonly string _pipeName;

    public GuardPipeClient(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        _pipeName = pipeName;
    }

    public async Task<GuardCommandResponse> SendAsync(
        GuardCommandRequest request,
        int connectTimeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        await client.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(request, GuardContractsJsonContext.DefaultContext.GuardCommandRequest);
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("Guard control pipe closed without returning a response.");

        return JsonSerializer.Deserialize(line, GuardContractsJsonContext.DefaultContext.GuardCommandResponse)
            ?? throw new InvalidDataException("Failed to deserialize GuardCommandResponse.");
    }
}
