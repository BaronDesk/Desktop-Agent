using BaronDeskAgent.ServiceCore;
using BaronDeskAgent.ServiceCore.Hardware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<HardwareSensorReader>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<HardwareMonitorService>();
builder.Services.AddHostedService<UsbMonitorService>();

var host = builder.Build();

await host.RunAsync();