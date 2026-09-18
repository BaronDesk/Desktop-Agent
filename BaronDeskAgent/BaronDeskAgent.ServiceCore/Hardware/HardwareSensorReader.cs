using BaronDesk.Shared.Models;
using LibreHardwareMonitor.Hardware;

namespace BaronDeskAgent.ServiceCore.Hardware;

public class HardwareSensorReader
{
    private readonly Computer _computer;

    public HardwareSensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };
    }

    public void Start()
    {
        _computer.Open();
    }

    public void Stop()
    {
        _computer.Close();
    }

    public HardwareTelemetry ReadTelemetry()
    {
        var telemetry = new HardwareTelemetry
        {
            Timestamp = DateTime.UtcNow
        };

        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();

            ReadHardware(hardware, telemetry);

            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Update();

                ReadHardware(subHardware, telemetry);
            }
        }

        return telemetry;
    }
    private static void ReadHardware(
    IHardware hardware,
    HardwareTelemetry telemetry)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is null)
                continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:

                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        telemetry.CpuTemperature ??= sensor.Value;
                    }
                    else if (hardware.HardwareType is
                             HardwareType.GpuAmd or
                             HardwareType.GpuNvidia or
                             HardwareType.GpuIntel)
                    {
                        telemetry.GpuTemperature ??= sensor.Value;
                    }

                    break;

                case SensorType.Load:

                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        telemetry.CpuLoad ??= sensor.Value;
                    }
                    else if (hardware.HardwareType is
                             HardwareType.GpuAmd or
                             HardwareType.GpuNvidia or
                             HardwareType.GpuIntel)
                    {
                        telemetry.GpuLoad ??= sensor.Value;
                    }

                    break;

                case SensorType.Fan:

                    telemetry.FanSpeed ??= sensor.Value;

                    break;
            }
        }
    }
}

