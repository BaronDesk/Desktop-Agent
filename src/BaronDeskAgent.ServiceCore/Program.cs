using BaronDeskAgent.ServiceCore.AntiTheft;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Credentials;
using BaronDeskAgent.ServiceCore.Enrollment;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Ipc;
using BaronDeskAgent.ServiceCore.Persistence;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Power;
using BaronDeskAgent.ServiceCore.Session;
using BaronDeskAgent.ServiceCore.Telemetry;
using BaronDeskAgent.ServiceCore.Telemetry.Outbox;
using Microsoft.Extensions.Options;

// Provisioning (--set/--clear-enrollment-token, --set/--clear-station-token) runs instead of the agent.
if (CredentialCommandLine.TryRun(args, out var exitCode))
{
    return exitCode;
}

var builder = Host.CreateApplicationBuilder(args);
var services = builder.Services;

// Configuration (fails fast on unsafe settings, e.g. TLS validation disabled outside Development)
services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection(AgentOptions.SectionName)).ValidateOnStart();
services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();
services.AddSingleton(TimeProvider.System);

// Persistence & policy
services.AddSingleton<AgentDatabase>();
services.AddSingleton<DatabaseInitializer>();
services.AddSingleton<DevelopmentDataSeeder>();
services.AddSingleton<PolicyRepository>();
services.AddSingleton<IPolicyStore, PolicyStore>();

// Station identity & enrollment
services.AddSingleton<IStationCredentialStore, DpapiStationCredentialStore>();
services.AddSingleton<IEnrollmentTokenStore, DpapiEnrollmentTokenStore>();
services.AddSingleton<IStationKeyStore, DpapiStationKeyStore>();
services.AddSingleton<IEnrollmentClient, HttpEnrollmentClient>();
services.AddSingleton<EnrollmentService>();

// Server connection
services.AddSingleton<ServerClock>();
services.AddSingleton<ReplayGuard>();
services.AddSingleton<IServerConnection, WebSocketConnection>();

// Station state: lock, session, lease, login relay
services.AddSingleton<PipeServer>();
services.AddSingleton<ILockScreen>(provider => provider.GetRequiredService<PipeServer>());
services.AddSingleton<LockService>();
services.AddSingleton<SessionService>();
services.AddSingleton<LeaseManager>();
services.AddSingleton<StationController>();
services.AddSingleton<LoginRelay>();

// Games & power
services.AddSingleton<GameCatalogRepository>();
services.AddSingleton<InteractiveProcessLauncher>();
services.AddSingleton<GameService>();
services.AddSingleton<SystemPowerService>();

// Commands
services.AddSingleton<CommandOutcomeStore>();
services.AddSingleton<ICommandHandler, LockCommandHandler>();
services.AddSingleton<ICommandHandler, UnlockCommandHandler>();
services.AddSingleton<ICommandHandler, EndSessionCommandHandler>();
services.AddSingleton<ICommandHandler, LaunchGameCommandHandler>();
services.AddSingleton<ICommandHandler, ShutdownCommandHandler>();
services.AddSingleton<ICommandHandler, PolicyUpdateCommandHandler>();
services.AddSingleton<CommandDispatcher>();

// Telemetry & outbox
services.AddSingleton<OutboxRepository>();
services.AddSingleton<OutboxQueue>();
services.AddSingleton<TelemetryPublisher>();
services.AddSingleton<HardwareSensorReader>();
services.AddSingleton<HardwareTelemetryMapper>();
services.AddSingleton<TelemetryDeltaFilter>();
services.AddSingleton<HardwareAlertEvaluator>();

// Background workers
services.AddHostedService(provider => provider.GetRequiredService<PipeServer>());
services.AddHostedService<ConnectionWorker>();
services.AddHostedService<HeartbeatWorker>();
services.AddHostedService<OutboxWorker>();
services.AddHostedService<HardwareMonitorService>();
services.AddHostedService<UsbMonitorService>();

var host = builder.Build();

await InitializeLocalStateAsync(host.Services, builder.Environment);
await host.RunAsync();
return 0;

// Runs before any worker starts, so workers see the persisted policy and handled-command ids.
static async Task InitializeLocalStateAsync(IServiceProvider provider, IHostEnvironment environment)
{
    await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

    if (environment.IsDevelopment())
    {
        await provider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync();
    }

    await provider.GetRequiredService<IPolicyStore>().InitializeAsync();
    await provider.GetRequiredService<CommandOutcomeStore>().LoadAsync();

    // Starts the lease watchdog immediately (unlocked ⇒ valid lease).
    provider.GetRequiredService<StationController>();
}
