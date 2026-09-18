using BaronDeskAgent.ServiceCore;
using BaronDeskAgent.ServiceCore.Hardware;
using BaronDeskAgent.ServiceCore.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<HardwareSensorReader>();

builder.Services.AddSingleton<
    ITelemetryTransport,
    LoggingTelemetryTransport>();

builder.Services.AddSingleton<TelemetryService>();

builder.Services.AddHostedService(
    serviceProvider =>
        serviceProvider.GetRequiredService<TelemetryService>());

builder.Services.AddHostedService<Worker>();

builder.Services.AddHostedService<
    HardwareMonitorService>();

builder.Services.AddHostedService<
    WindowsDeviceMonitorService>();

var host = builder.Build();

await host.RunAsync();