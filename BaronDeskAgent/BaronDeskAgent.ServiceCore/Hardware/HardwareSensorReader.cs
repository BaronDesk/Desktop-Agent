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
            Timestamp = DateTimeOffset.UtcNow
        };

        var visitor = new UpdateVisitor();

        _computer.Accept(visitor);

        List<IHardware> allHardware =
            EnumerateHardware(_computer.Hardware)
                .ToList();

        foreach (IHardware hardware in allHardware)
        {
            ProcessNonCpuHardware(
                hardware,
                telemetry);
        }

        ProcessCpu(
            allHardware,
            telemetry);

        FinalizeRamTelemetry(
            telemetry);

        return telemetry;
    }

    private static IEnumerable<IHardware> EnumerateHardware(
        IEnumerable<IHardware> hardwareCollection)
    {
        foreach (IHardware hardware in hardwareCollection)
        {
            yield return hardware;

            foreach (IHardware subHardware
                     in EnumerateHardware(hardware.SubHardware))
            {
                yield return subHardware;
            }
        }
    }

    private static void ProcessNonCpuHardware(
        IHardware hardware,
        HardwareTelemetry telemetry)
    {
        if (hardware.HardwareType == HardwareType.Cpu)
        {
            return;
        }

        switch (hardware.HardwareType)
        {
            case HardwareType.Memory:

                ProcessMemory(
                    hardware,
                    telemetry);

                break;

            case HardwareType.GpuNvidia:

                ProcessGpu(
                    hardware,
                    telemetry,
                    "NVIDIA");

                break;

            case HardwareType.GpuAmd:

                ProcessGpu(
                    hardware,
                    telemetry,
                    "AMD");

                break;

            case HardwareType.GpuIntel:

                ProcessGpu(
                    hardware,
                    telemetry,
                    "Intel");

                break;
        }

        ProcessFans(
            hardware,
            telemetry);
    }

    private static void ProcessCpu(
        IEnumerable<IHardware> allHardware,
        HardwareTelemetry telemetry)
    {
        IHardware? cpu =
            allHardware.FirstOrDefault(
                h => h.HardwareType == HardwareType.Cpu);

        if (cpu is null)
        {
            return;
        }

        telemetry.Cpu.Name =
            cpu.Name;

        telemetry.Cpu.Vendor =
            DetectCpuVendor(cpu.Name);

        List<ISensor> sensors =
            cpu.Sensors
                .Where(s => s.Value.HasValue)
                .ToList();

        ISensor? temperatureSensor =
            sensors
                .Where(s =>
                    s.SensorType ==
                    SensorType.Temperature &&
                    s.Value.HasValue &&
                    s.Value.Value > 0)
                .OrderBy(
                    s => GetCpuTemperaturePriority(
                        s.Name))
                .FirstOrDefault();

        if (temperatureSensor is not null)
        {
            telemetry.Cpu.TemperatureC =
                Math.Round(
                    temperatureSensor.Value!.Value,
                    2);
        }

        ISensor? totalLoadSensor =
            sensors
                .Where(s =>
                    s.SensorType ==
                    SensorType.Load &&
                    s.Value.HasValue)
                .OrderBy(
                    s => GetCpuLoadPriority(
                        s.Name))
                .FirstOrDefault();

        if (totalLoadSensor is not null &&
            !IsCpuCoreLoadSensor(
                totalLoadSensor.Name))
        {
            telemetry.Cpu.LoadPercent =
                Math.Round(
                    totalLoadSensor.Value!.Value,
                    2);
        }

        ISensor? coreMaxSensor =
            sensors.FirstOrDefault(s =>
                s.SensorType ==
                    SensorType.Load &&
                s.Value.HasValue &&
                s.Name.Equals(
                    "CPU Core Max",
                    StringComparison.OrdinalIgnoreCase));

        if (coreMaxSensor is not null)
        {
            telemetry.Cpu.CoreMaxLoadPercent =
                Math.Round(
                    coreMaxSensor.Value!.Value,
                    2);

            return;
        }

        List<double> coreLoads =
            sensors
                .Where(s =>
                    s.SensorType ==
                        SensorType.Load &&
                    s.Value.HasValue &&
                    IsCpuCoreLoadSensor(
                        s.Name))
                .Select(
                    s => (double)s.Value!.Value)
                .ToList();

        if (coreLoads.Count > 0)
        {
            telemetry.Cpu.CoreMaxLoadPercent =
                Math.Round(
                    coreLoads.Max(),
                    2);
        }
    }

    private static void ProcessMemory(
        IHardware hardware,
        HardwareTelemetry telemetry)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            double value =
                sensor.Value.Value;

            string name =
                sensor.Name.Trim();

            if (sensor.SensorType ==
                SensorType.Data)
            {
                if (name.Equals(
                        "Memory Used",
                        StringComparison.OrdinalIgnoreCase))
                {
                    telemetry.Ram.UsedGb =
                        Math.Round(
                            value,
                            2);
                }
                else if (name.Equals(
                    "Memory Available",
                    StringComparison.OrdinalIgnoreCase))
                {
                    telemetry.Ram.AvailableGb =
                        Math.Round(
                            value,
                            2);
                }
            }
            else if (sensor.SensorType ==
                     SensorType.Load)
            {
                if (name.Equals(
                        "Memory",
                        StringComparison.OrdinalIgnoreCase))
                {
                    telemetry.Ram.UsagePercent =
                        Math.Round(
                            value,
                            2);
                }
            }
        }
    }

    private static void ProcessGpu(
        IHardware hardware,
        HardwareTelemetry telemetry,
        string vendor)
    {
        var gpu =
            new GpuTelemetry
            {
                Name = hardware.Name,
                Vendor = vendor
            };

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            double value =
                sensor.Value.Value;

            string name =
                sensor.Name.Trim();

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:

                    ProcessGpuTemperature(
                        name,
                        value,
                        gpu);

                    break;

                case SensorType.Load:

                    ProcessGpuLoad(
                        name,
                        value,
                        gpu);

                    break;

                case SensorType.SmallData:

                    ProcessGpuMemory(
                        name,
                        value,
                        gpu);

                    break;
            }
        }

        telemetry.Gpus.Add(gpu);
    }

    private static void ProcessGpuTemperature(
        string name,
        double value,
        GpuTelemetry gpu)
    {
        value = Math.Round(value, 2);

        if (name.Equals(
                "GPU Core",
                StringComparison.OrdinalIgnoreCase))
        {
            gpu.TemperatureC =
                value;
        }
        else if (name.Equals(
            "GPU Hot Spot",
            StringComparison.OrdinalIgnoreCase))
        {
            gpu.HotSpotTemperatureC =
                value;
        }
        else if (name.Equals(
            "GPU Memory Junction",
            StringComparison.OrdinalIgnoreCase))
        {
            gpu.MemoryTemperatureC =
                value;
        }
        else if (!gpu.TemperatureC.HasValue &&
                 name.Contains(
                     "GPU",
                     StringComparison.OrdinalIgnoreCase))
        {
            gpu.TemperatureC =
                value;
        }
    }

    private static void ProcessGpuLoad(
        string name,
        double value,
        GpuTelemetry gpu)
    {
        if (name.Equals(
                "GPU Core",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "D3D 3D",
                StringComparison.OrdinalIgnoreCase))
        {
            gpu.LoadPercent =
                Math.Round(
                    value,
                    2);
        }
    }

    private static void ProcessGpuMemory(
        string name,
        double value,
        GpuTelemetry gpu)
    {
        value = Math.Round(value, 2);

        if (name.Equals(
                "GPU Memory Used",
                StringComparison.OrdinalIgnoreCase))
        {
            gpu.MemoryUsedMb =
                value;
        }
        else if (name.Equals(
            "GPU Memory Free",
            StringComparison.OrdinalIgnoreCase))
        {
            gpu.MemoryFreeMb =
                value;
        }
        else if (name.Equals(
            "GPU Memory Total",
            StringComparison.OrdinalIgnoreCase))
        {
            gpu.MemoryTotalMb =
                value;
        }
    }

    private static void ProcessFans(
        IHardware hardware,
        HardwareTelemetry telemetry)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType !=
                SensorType.Fan ||
                !sensor.Value.HasValue)
            {
                continue;
            }

            telemetry.Fans.Add(
                new FanTelemetry
                {
                    Name = sensor.Name,
                    SpeedRpm =
                        Math.Round(
                            sensor.Value.Value,
                            2)
                });
        }
    }

    private static string DetectCpuVendor(
        string cpuName)
    {
        if (cpuName.Contains(
                "AMD",
                StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (cpuName.Contains(
                "Intel",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return "Unknown";
    }

    private static int GetCpuTemperaturePriority(
        string name)
    {
        if (name.Equals(
                "CPU Package",
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Equals(
                "Package",
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.Equals(
                "Core Average",
                StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.Equals(
                "CPU Core",
                StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.Equals(
                "Core (Tctl/Tdie)",
                StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (name.Contains(
                "Tctl/Tdie",
                StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (name.Contains(
                "Package",
                StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        if (name.Contains(
                "Core",
                StringComparison.OrdinalIgnoreCase))
        {
            return 7;
        }

        return 100;
    }

    private static int GetCpuLoadPriority(
        string name)
    {
        if (name.Equals(
                "CPU Total",
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Equals(
                "Total",
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.Equals(
                "Processor Total",
                StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (IsCpuCoreLoadSensor(name))
        {
            return 100;
        }

        return 10;
    }

    private static bool IsCpuCoreLoadSensor(
        string name)
    {
        return
            name.Contains(
                "Core",
                StringComparison.OrdinalIgnoreCase)
            ||
            name.Contains(
                "Thread",
                StringComparison.OrdinalIgnoreCase);
    }

    private static void FinalizeRamTelemetry(
        HardwareTelemetry telemetry)
    {
        if (telemetry.Ram.UsedGb.HasValue &&
            telemetry.Ram.AvailableGb.HasValue)
        {
            telemetry.Ram.TotalGb =
                Math.Round(
                    telemetry.Ram.UsedGb.Value +
                    telemetry.Ram.AvailableGb.Value,
                    2);

            if (!telemetry.Ram.UsagePercent.HasValue &&
                telemetry.Ram.TotalGb.Value > 0)
            {
                telemetry.Ram.UsagePercent =
                    Math.Round(
                        telemetry.Ram.UsedGb.Value /
                        telemetry.Ram.TotalGb.Value *
                        100.0,
                        2);
            }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(
            IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(
            IHardware hardware)
        {
            hardware.Update();
        }

        public void VisitSensor(
            ISensor sensor)
        {
        }

        public void VisitParameter(
            IParameter parameter)
        {
        }
    }
}