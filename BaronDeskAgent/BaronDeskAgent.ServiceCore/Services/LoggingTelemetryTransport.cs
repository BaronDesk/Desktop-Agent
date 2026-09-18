using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services;

public class LoggingTelemetryTransport : ITelemetryTransport
{
    private readonly ILogger<LoggingTelemetryTransport> _logger;

    public LoggingTelemetryTransport(
        ILogger<LoggingTelemetryTransport> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(
        TelemetryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        switch (envelope.Payload)
        {
            case HardwareTelemetry hardware:

                LogHardwareTelemetry(
                    hardware);

                break;

            case DeviceTelemetry device:

                LogDeviceTelemetry(
                    device);

                break;

            default:

                _logger.LogInformation(
                    "Telemetry | Type: {Type} | Seq: {Seq}",
                    envelope.Type,
                    envelope.Seq);

                break;
        }

        return Task.CompletedTask;
    }

    private void LogHardwareTelemetry(
        HardwareTelemetry telemetry)
    {
        string cpu =
            $"{telemetry.Cpu.Vendor} " +
            $"{telemetry.Cpu.Name}";

        string cpuLoad =
            FormatPercent(
                telemetry.Cpu.LoadPercent);

        string cpuTemp =
            FormatTemperature(
                telemetry.Cpu.TemperatureC);

        string cpuCoreMax =
            FormatPercent(
                telemetry.Cpu.CoreMaxLoadPercent);

        string ram =
            telemetry.Ram.UsedGb.HasValue &&
            telemetry.Ram.TotalGb.HasValue
                ? $"{telemetry.Ram.UsedGb.Value:F2}/" +
                  $"{telemetry.Ram.TotalGb.Value:F2} GB"
                : "N/A";

        string ramPercent =
            FormatPercent(
                telemetry.Ram.UsagePercent);

        _logger.LogInformation(
            "Hardware | CPU: {Cpu} | Load: {CpuLoad} | " +
            "Temp: {CpuTemp} | Core Max: {CpuCoreMax} | " +
            "RAM: {Ram} ({RamPercent})",
            cpu,
            cpuLoad,
            cpuTemp,
            cpuCoreMax,
            ram,
            ramPercent);

        foreach (GpuTelemetry gpu
                 in telemetry.Gpus)
        {
            string gpuMemory =
                gpu.MemoryUsedMb.HasValue &&
                gpu.MemoryTotalMb.HasValue
                    ? $"{gpu.MemoryUsedMb.Value:F0}/" +
                      $"{gpu.MemoryTotalMb.Value:F0} MB"
                    : "N/A";

            _logger.LogInformation(
                "GPU | {Vendor} | {Name} | Load: {Load} | " +
                "Temp: {Temperature} | VRAM: {Memory}",
                gpu.Vendor,
                gpu.Name,
                FormatPercent(
                    gpu.LoadPercent),
                FormatTemperature(
                    gpu.TemperatureC),
                gpuMemory);

            if (gpu.HotSpotTemperatureC.HasValue)
            {
                _logger.LogInformation(
                    "GPU | {Name} | Hot Spot: {Temperature}",
                    gpu.Name,
                    FormatTemperature(
                        gpu.HotSpotTemperatureC));
            }

            if (gpu.MemoryTemperatureC.HasValue)
            {
                _logger.LogInformation(
                    "GPU | {Name} | Memory Temp: {Temperature}",
                    gpu.Name,
                    FormatTemperature(
                        gpu.MemoryTemperatureC));
            }
        }

        foreach (FanTelemetry fan
                 in telemetry.Fans)
        {
            _logger.LogInformation(
                "Fan | {Name} | Speed: {Speed}",
                fan.Name,
                fan.SpeedRpm.HasValue
                    ? $"{fan.SpeedRpm.Value:F0} RPM"
                    : "N/A");
        }
    }

    private void LogDeviceTelemetry(
        DeviceTelemetry telemetry)
    {
        string productId =
            string.IsNullOrWhiteSpace(
                telemetry.ProductId)
                ? "N/A"
                : telemetry.ProductId;

        _logger.LogInformation(
            "Device {EventType} | {DeviceType} | " +
            "{DeviceName} | PID: {ProductId}",
            telemetry.EventType,
            telemetry.DeviceType,
            telemetry.DeviceName,
            productId);
    }

    private static string FormatPercent(
        double? value)
    {
        return value.HasValue
            ? $"{value.Value:F2}%"
            : "N/A";
    }

    private static string FormatTemperature(
        double? value)
    {
        return value.HasValue
            ? $"{value.Value:F1}°C"
            : "N/A";
    }
}