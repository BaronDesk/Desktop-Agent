using BaronDeskAgent.ServiceCore;
using BaronDeskAgent.ServiceCore.Hardware;
using BaronDeskAgent.ServiceCore.Services;

var builder = Host.CreateApplicationBuilder(args);

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
// Background monitoring
// ---------------------------------------------------------

builder.Services.AddHostedService<HardwareMonitorService>();

builder.Services.AddHostedService<WindowsDeviceMonitorService>();

// ---------------------------------------------------------
// Existing Worker
// ---------------------------------------------------------

builder.Services.AddHostedService<Worker>();

// ---------------------------------------------------------
// Build and run
// ---------------------------------------------------------

var host = builder.Build();

await host.RunAsync();