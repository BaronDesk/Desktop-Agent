using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Communication;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Repositories;
using BaronDeskAgent.ServiceCore.Hardware;
using BaronDeskAgent.ServiceCore.Security;
using BaronDeskAgent.ServiceCore.Services;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Games;
using BaronDeskAgent.ServiceCore.Services.Outbox;
using BaronDeskAgent.ServiceCore.Services.Session;
using BaronDeskAgent.ServiceCore.Services.System;
using BaronDeskAgent.ServiceCore.Services.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

// ---------------------------------------------------------
// Configuration
// ---------------------------------------------------------

builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

// ---------------------------------------------------------
// Database
// ---------------------------------------------------------

builder.Services.AddSingleton<AgentDatabase>();

builder.Services.AddSingleton<DatabaseInitializer>();

builder.Services.AddSingleton<OutboxRepository>();

// ---------------------------------------------------------
// Security & Connection
// ---------------------------------------------------------

builder.Services.AddSingleton<ReplayGuard>();

builder.Services.AddSingleton<IdempotencyTracker>();

builder.Services.AddSingleton<IServerConnection, WebSocketConnection>();

// ---------------------------------------------------------
// Hardware
// ---------------------------------------------------------

builder.Services.AddSingleton<HardwareSensorReader>();

// ---------------------------------------------------------
// Telemetry
// ---------------------------------------------------------

builder.Services.AddSingleton<ITelemetryTransport, WebSocketTelemetryTransport>();

builder.Services.AddSingleton<HardwareTelemetryMapper>();

builder.Services.AddSingleton<TelemetryService>();

// ---------------------------------------------------------
// Outbox
// ---------------------------------------------------------

builder.Services.AddHostedService<OutboxWorker>();

// ---------------------------------------------------------
// Commands
// ---------------------------------------------------------

builder.Services.AddSingleton<ICommandHandler, LockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, UnlockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, EndSessionCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, LaunchGameCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, ShutdownCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, PolicyUpdateCommandHandler>();

builder.Services.AddSingleton<CommandService>();

// ---------------------------------------------------------
// Command Services, Session & Lease Management
// ---------------------------------------------------------

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<LeaseManager>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<SystemPowerService>();

// ---------------------------------------------------------
// Monitoring & Communication Workers
// ---------------------------------------------------------

builder.Services.AddHostedService<HardwareMonitorService>();

builder.Services.AddHostedService<WindowsDeviceMonitorService>();

builder.Services.AddHostedService<ConnectionWorker>();

builder.Services.AddHostedService<HeartbeatWorker>();

// ---------------------------------------------------------
// Build
// ---------------------------------------------------------

var host = builder.Build();

// ---------------------------------------------------------
// Initialize SQLite
// ---------------------------------------------------------

using (var scope = host.Services.CreateScope())
{
    var databaseInitializer =
        scope.ServiceProvider
            .GetRequiredService<DatabaseInitializer>();

    await databaseInitializer.InitializeAsync();
}

// ---------------------------------------------------------
// Run
// ---------------------------------------------------------

await host.RunAsync();