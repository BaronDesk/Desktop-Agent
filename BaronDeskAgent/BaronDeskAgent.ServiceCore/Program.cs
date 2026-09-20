using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Repositories;
using BaronDeskAgent.ServiceCore.Hardware;
using BaronDeskAgent.ServiceCore.Services;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Games;
using BaronDeskAgent.ServiceCore.Services.Outbox;
using BaronDeskAgent.ServiceCore.Services.Session;
using BaronDeskAgent.ServiceCore.Services.System;
using BaronDeskAgent.ServiceCore.Services.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

// ---------------------------------------------------------
// Database
// ---------------------------------------------------------

builder.Services.AddSingleton<AgentDatabase>();

builder.Services.AddSingleton<DatabaseInitializer>();

builder.Services.AddSingleton<OutboxRepository>();

// ---------------------------------------------------------
// Hardware
// ---------------------------------------------------------

builder.Services.AddSingleton<HardwareSensorReader>();

// ---------------------------------------------------------
// Telemetry
// ---------------------------------------------------------

builder.Services.AddSingleton<
    ITelemetryTransport,
    LoggingTelemetryTransport>();

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
// Command Services
// ---------------------------------------------------------

builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<SystemPowerService>();

// ---------------------------------------------------------
// Monitoring
// ---------------------------------------------------------

builder.Services.AddHostedService<HardwareMonitorService>();

builder.Services.AddHostedService<WindowsDeviceMonitorService>();



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