namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>alert.category</c> values (FROZEN). The Prisma schema has no <c>category</c> column yet (V4 drift).
/// </summary>
public static class AlertCategories
{
    public const string Hardware = "hardware";
    public const string AntiTheft = "anti_theft";
    public const string SecurityViolation = "security_violation";
}

/// <summary>
/// <c>alert.severity</c> values, mirroring the Prisma <c>AlertSeverity</c> enum.
/// </summary>
public static class AlertSeverities
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";
}

/// <summary>
/// <c>alert.type</c> values, mirroring the Prisma <c>TelemetryAlertType</c> enum.
/// The schema has no anti-theft or security-violation types yet, so those alerts use
/// <see cref="HardwareFailure"/> and are told apart by their category.
/// </summary>
public static class AlertTypes
{
    public const string CpuUsage = "CPU_USAGE";
    public const string MemoryUsage = "MEMORY_USAGE";
    public const string HardwareFailure = "HARDWARE_FAILURE";
    public const string TemperatureWarning = "TEMPERATURE_WARNING";
}
