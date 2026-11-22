namespace LocalLlmConsole.Services;

public enum ExecutionBackendMode
{
    ExternalLlamaSwap,
    LegacyNativeForUnitTestsOnly
}

public sealed class NativeProcessControlBlockedException : InvalidOperationException
{
    public string BlockedOperation { get; }

    public NativeProcessControlBlockedException(string operation)
        : base($"[ExternalLlamaSwap P0 Isolation] Native process control operation '{operation}' is permanently blocked. All model lifecycle actions must route exclusively through LocalLLMManager.Guard -> llama-swap v275.")
    {
        BlockedOperation = operation;
    }
}

/// <summary>
/// Phase 4B-1 P0 Isolation Gate.
/// In the desktop application (App.OnStartup), LockExternalLlamaSwapForRuntime() irrevocably locks the entire
/// process into ExternalLlamaSwap mode across all threads. In parallel test runners, UseModeForTests() uses
/// AsyncLocal scoping so Phase 4B isolation tests run in parallel without mutating unrelated legacy unit tests.
/// </summary>
public static class ExternalExecutionPolicy
{
    private static int _lockedToExternal;
    private static readonly AsyncLocal<ExecutionBackendMode?> ScopedTestMode = new();

    public static bool IsExternalLlamaSwapMode
        => Volatile.Read(ref _lockedToExternal) != 0
            || ScopedTestMode.Value == ExecutionBackendMode.ExternalLlamaSwap;

    public static bool IsIrrevocablyLocked
        => Volatile.Read(ref _lockedToExternal) != 0;

    /// <summary>Irrevocably locks the running desktop process into ExternalLlamaSwap mode (cannot be undone).</summary>
    public static void LockExternalLlamaSwapForRuntime()
        => Volatile.Write(ref _lockedToExternal, 1);

    /// <summary>AsyncLocal-scoped override for Phase 4B automated tests when not locked by App.OnStartup.</summary>
    internal static IDisposable UseModeForTests(ExecutionBackendMode mode)
    {
        if (IsIrrevocablyLocked && mode != ExecutionBackendMode.ExternalLlamaSwap)
            throw new InvalidOperationException("Cannot exit ExternalLlamaSwap mode once locked for runtime.");

        var previous = ScopedTestMode.Value;
        ScopedTestMode.Value = mode;
        return new Scope(() => ScopedTestMode.Value = previous);
    }

    public static void ThrowIfNativeProcessControlBlocked(string operationName)
    {
        if (IsExternalLlamaSwapMode)
            throw new NativeProcessControlBlockedException(operationName);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

/// <summary>
/// Phase 4B-1 NullModelGatewayHost.
/// Replaces the built-in ModelGatewayService in ExternalLlamaSwap mode so the desktop app binds ZERO OpenAI gateway ports
/// and llama-swap v275 remains the sole fixed API data plane (127.0.0.1:19000/v1).
/// </summary>
public sealed class NullModelGatewayHost : IModelGatewayHost
{
    public bool IsListening => false;
    public string LastListenerError => "";
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
