using BaronDeskAgent.ServiceCore;
using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Repositories;
using BaronDeskAgent.ServiceCore.Hardware;
using BaronDeskAgent.ServiceCore.Services;
using BaronDeskAgent.ServiceCore.Services.Outbox;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;

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

builder.Services.AddSingleton<TelemetryService>();

builder.Services.AddHostedService(
    serviceProvider =>
        serviceProvider.GetRequiredService<TelemetryService>());

// ---------------------------------------------------------
// Outbox
// ---------------------------------------------------------

builder.Services.AddHostedService<OutboxWorker>();


// ---------------------------------------------------------
// Commands
// ---------------------------------------------------------

builder.Services.AddSingleton<ICommandHandler, LockCommandHandler>();
builder.Services.AddSingleton<CommandService>();

// ---------------------------------------------------------
// Monitoring
// ---------------------------------------------------------

builder.Services.AddHostedService<HardwareMonitorService>();

builder.Services.AddHostedService<WindowsDeviceMonitorService>();

// ---------------------------------------------------------
// Existing Worker
// ---------------------------------------------------------

builder.Services.AddHostedService<Worker>();

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