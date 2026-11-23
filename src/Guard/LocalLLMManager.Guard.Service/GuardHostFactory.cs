using System.Net;
using LocalLLMManager.Guard.Contracts;
using LocalLLMManager.Guard.Service.Control;
using LocalLLMManager.Guard.Service.Discovery;
using LocalLLMManager.Guard.Service.Security;
using LocalLLMManager.Guard.Service.Workers;

namespace LocalLLMManager.Guard.Service;

public sealed class GuardHostConfig
{
    public string StateDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "guard_state");
    public string PipeName { get; init; } = GuardPipeClient.DefaultProductionPipeName;
    public int AgentFacadeListenPort { get; init; } = 19250;
    public string InternalLlamaSwapBaseUrl { get; init; } = "http://127.0.0.1:19259";
    public bool RequireAgentInferenceApiKey { get; init; } = true;
    public GuardCredentialSet? OverrideCredentials { get; init; }
    public IReadOnlyDictionary<string, GuardProfileSpec> Profiles { get; init; } = new Dictionary<string, GuardProfileSpec>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> DiscoveryTargetImageNames { get; init; } = ["llama-server.exe"];
    public int? DiscoveryPortRangeMin { get; init; }
    public int? DiscoveryPortRangeMax { get; init; }
    public Func<int?>? ManagedGatewayPidProvider { get; init; }
    public IExternalProcessDiscoveryService? CustomDiscovery { get; init; }
    public IHostTelemetryCollector? CustomTelemetry { get; init; }
    public int DrainTimeoutSeconds { get; init; } = 15;
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class GuardRuntimeHost : IAsyncDisposable
{
    public WebApplication App { get; }
    public GuardCredentialStore CredentialStore { get; }
    public GuardStateStore StateStore { get; }
    public GuardControlCoordinator Coordinator { get; }
    public GuardInferenceFacadeMiddleware Facade { get; }
    public string PipeName { get; }
    public int AgentFacadePort { get; }

    public GuardRuntimeHost(
        WebApplication app,
        GuardCredentialStore credentialStore,
        GuardStateStore stateStore,
        GuardControlCoordinator coordinator,
        GuardInferenceFacadeMiddleware facade,
        string pipeName,
        int agentFacadePort)
    {
        App = app;
        CredentialStore = credentialStore;
        StateStore = stateStore;
        Coordinator = coordinator;
        Facade = facade;
        PipeName = pipeName;
        AgentFacadePort = agentFacadePort;
    }

    public Task StartAsync(CancellationToken ct = default) => App.StartAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => App.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync().ConfigureAwait(false); } catch { }
        await App.DisposeAsync().ConfigureAwait(false);
        Facade.Dispose();
        Coordinator.Dispose();
    }
}

public static class GuardHostFactory
{
    public static GuardRuntimeHost Build(GuardHostConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var credStore = new GuardCredentialStore(cfg.StateDirectory, cfg.OverrideCredentials);
        string?[] SecretsProvider() =>
        [
            credStore.Credentials.InternalLlamaSwapAdminKey,
            credStore.Credentials.AgentInferenceApiKey
        ];

        var stateStore = new GuardStateStore(cfg.StateDirectory, SecretsProvider);
        var adminClient = new LlamaSwapAdminClient(cfg.InternalLlamaSwapBaseUrl, credStore.Credentials.InternalLlamaSwapAdminKey);

        var discovery = cfg.CustomDiscovery ?? new WindowsProcessDiscoveryService(new ProcessDiscoveryOptions
        {
            TargetImageNames = cfg.DiscoveryTargetImageNames,
            PortRangeMin = cfg.DiscoveryPortRangeMin,
            PortRangeMax = cfg.DiscoveryPortRangeMax,
            ManagedGatewayPidProvider = cfg.ManagedGatewayPidProvider,
            KnownSecretsProvider = SecretsProvider
        });

        var telemetry = cfg.CustomTelemetry ?? new HostTelemetryCollector();

        var coordinator = new GuardControlCoordinator(
            new GuardCoordinatorOptions
            {
                StableLogicalModelId = "local-coder",
                Profiles = cfg.Profiles,
                ManagedGatewayPidProvider = cfg.ManagedGatewayPidProvider,
                KnownSecretsProvider = SecretsProvider,
                DrainTimeoutSeconds = cfg.DrainTimeoutSeconds
            },
            adminClient,
            discovery,
            telemetry,
            stateStore);

        var facade = new GuardInferenceFacadeMiddleware(new GuardInferenceFacadeOptions
        {
            InternalLlamaSwapBaseUrl = cfg.InternalLlamaSwapBaseUrl,
            InternalLlamaSwapAdminKey = credStore.Credentials.InternalLlamaSwapAdminKey,
            AgentInferenceApiKey = credStore.Credentials.AgentInferenceApiKey,
            RequireAgentInferenceApiKey = cfg.RequireAgentInferenceApiKey
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, cfg.AgentFacadeListenPort);
        });

        builder.Services.AddSingleton(coordinator);
        builder.Services.AddHostedService(_ => new NamedPipeControlServer(cfg.PipeName, coordinator, SecretsProvider));
        builder.Services.AddHostedService(_ => new PeriodicDiscoveryWorker(coordinator, cfg.DiscoveryInterval));

        var app = builder.Build();
        app.Run(facade.HandleAsync);

        return new GuardRuntimeHost(app, credStore, stateStore, coordinator, facade, cfg.PipeName, cfg.AgentFacadeListenPort);
    }
}
