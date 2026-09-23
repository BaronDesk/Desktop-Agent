using LibreHardwareMonitor.Hardware;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Reads CPU/GPU/RAM sensors and fans through LibreHardwareMonitor. The <see cref="Computer"/> handle is opened
/// once and reused. Only the hardware groups that are reported are enabled (motherboard for fan headers).
/// </summary>
public sealed class HardwareSensorReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true
    };

    private readonly UpdateVisitor _updateVisitor = new();
    private bool _isOpen;

    /// <summary>Loads the sensor drivers. Throws when they are unavailable (no admin rights, driver blocked).</summary>
    public void Open()
    {
        _computer.Open();
        _isOpen = true;
    }

    public HardwareTelemetry Read(DateTimeOffset sampledAt)
    {
        var telemetry = new HardwareTelemetry { SampledAt = sampledAt };

        _computer.Accept(_updateVisitor);

        IHardware? cpu = null;
        foreach (var hardware in Flatten(_computer.Hardware))
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu ??= hardware;
                    break;
                case HardwareType.Memory:
                    ReadMemory(hardware, telemetry.Ram);
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    telemetry.Gpus.Add(ReadGpu(hardware));
                    break;
            }

            if (hardware.HardwareType != HardwareType.Cpu)
            {
                ReadFans(hardware, telemetry.Fans);
            }
        }

        if (cpu is not null)
        {
            ReadCpu(cpu, telemetry.Cpu);
        }

        CompleteRam(telemetry.Ram);
        return telemetry;
    }

    public void Dispose()
    {
        if (_isOpen)
        {
            _computer.Close();
            _isOpen = false;
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;

            foreach (var child in Flatten(item.SubHardware))
            {
                yield return child;
            }
        }
    }

    private static void ReadCpu(IHardware cpu, CpuTelemetry telemetry)
    {
        telemetry.Name = cpu.Name;

        ISensor? temperature = null;
        ISensor? totalLoad = null;
        double? coreMaxLoad = null;
        double? highestCoreLoad = null;

        foreach (var sensor in cpu.Sensors)
        {
            if (sensor.Value is not { } value)
            {
                continue;
            }

            if (sensor.SensorType == SensorType.Temperature && value > 0 &&
                (temperature is null || CpuTemperaturePriority(sensor.Name) < CpuTemperaturePriority(temperature.Name)))
            {
                temperature = sensor;
            }
            else if (sensor.SensorType == SensorType.Load)
            {
                if (sensor.Name.Equals("CPU Core Max", StringComparison.OrdinalIgnoreCase))
                {
                    coreMaxLoad = value;
                }
                else if (IsPerCoreLoad(sensor.Name))
                {
                    highestCoreLoad = Math.Max(highestCoreLoad ?? 0, value);
                }
                else if (totalLoad is null || CpuLoadPriority(sensor.Name) < CpuLoadPriority(totalLoad.Name))
                {
                    totalLoad = sensor;
                }
            }
        }

        telemetry.TemperatureC = Round(temperature?.Value);
        telemetry.LoadPercent = Round(totalLoad?.Value);
        telemetry.CoreMaxLoadPercent = Round(coreMaxLoad ?? highestCoreLoad);
    }

    private static void ReadMemory(IHardware memory, RamTelemetry telemetry)
    {
        foreach (var sensor in memory.Sensors)
        {
            if (sensor.Value is not { } value)
            {
                continue;
            }

            switch (sensor.SensorType, sensor.Name.Trim())
            {
                case (SensorType.Data, "Memory Used"):
                    telemetry.UsedGb = Round(value);
                    break;
                case (SensorType.Data, "Memory Available"):
                    telemetry.AvailableGb = Round(value);
                    break;
                case (SensorType.Load, "Memory"):
                    telemetry.UsagePercent = Round(value);
                    break;
            }
        }
    }

    private static GpuTelemetry ReadGpu(IHardware hardware)
    {
        var gpu = new GpuTelemetry { Name = hardware.Name };

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } raw)
            {
                continue;
            }

            var value = Round(raw);
            var name = sensor.Name.Trim();

            switch (sensor.SensorType)
            {
                case SensorType.Temperature when name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase):
                    gpu.TemperatureC = value;
                    break;
                case SensorType.Temperature when name.Equals("GPU Hot Spot", StringComparison.OrdinalIgnoreCase):
                    gpu.HotSpotTemperatureC = value;
                    break;
                case SensorType.Temperature when name.Equals("GPU Memory Junction", StringComparison.OrdinalIgnoreCase):
                    gpu.MemoryTemperatureC = value;
                    break;
                case SensorType.Temperature when gpu.TemperatureC is null && name.Contains("GPU", StringComparison.OrdinalIgnoreCase):
                    gpu.TemperatureC = value;
                    break;
                case SensorType.Load when name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                          name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase):
                    gpu.LoadPercent = value;
                    break;
                case SensorType.SmallData when name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase):
                    gpu.MemoryUsedMb = value;
                    break;
                case SensorType.SmallData when name.Equals("GPU Memory Free", StringComparison.OrdinalIgnoreCase):
                    gpu.MemoryFreeMb = value;
                    break;
                case SensorType.SmallData when name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase):
                    gpu.MemoryTotalMb = value;
                    break;
            }
        }

        return gpu;
    }

    private static void ReadFans(IHardware hardware, List<FanTelemetry> fans)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Fan && sensor.Value is { } speed)
            {
                fans.Add(new FanTelemetry { Name = sensor.Name, SpeedRpm = Round(speed) });
            }
        }
    }

    private static void CompleteRam(RamTelemetry ram)
    {
        if (ram.UsedGb is not { } used || ram.AvailableGb is not { } available)
        {
            return;
        }

        var total = used + available;
        ram.TotalGb = Round(total);
        if (ram.UsagePercent is null && total > 0)
        {
            ram.UsagePercent = Round(used / total * 100.0);
        }
    }

    // Lower is preferred. Package sensors first; "Tctl" on some Ryzen parts carries a +10..20 °C offset.
    private static int CpuTemperaturePriority(string name) => name switch
    {
        _ when name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) => 0,
        _ when name.Equals("Package", StringComparison.OrdinalIgnoreCase) => 1,
        _ when name.Equals("Core Average", StringComparison.OrdinalIgnoreCase) => 2,
        _ when name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase) => 3,
        _ when name.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) => 4,
        _ when name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) => 5,
        _ when name.Contains("Package", StringComparison.OrdinalIgnoreCase) => 6,
        _ when name.Contains("Core", StringComparison.OrdinalIgnoreCase) => 7,
        _ => 100
    };

    private static int CpuLoadPriority(string name) => name switch
    {
        _ when name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) => 0,
        _ when name.Equals("Total", StringComparison.OrdinalIgnoreCase) => 1,
        _ when name.Equals("Processor Total", StringComparison.OrdinalIgnoreCase) => 2,
        _ => 10
    };

    private static bool IsPerCoreLoad(string name) =>
        name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Thread", StringComparison.OrdinalIgnoreCase);

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;

    private static double Round(float value) => Math.Round(value, 2);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            // Sub-hardware (e.g. the motherboard's Super I/O chip, which carries the fan sensors) must be
            // updated explicitly, or its readings stay stale.
            foreach (var child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
