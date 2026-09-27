namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>alert.category</c> values (FROZEN). The Prisma <c>AlertCategory</c> enum spells them in upper case; the backend maps them.
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
/// <c>alert.type</c> values. <c>TelemetryAlert.type</c> is a free string in the Prisma schema, so anti-theft and
/// security alerts carry their own type instead of <see cref="HardwareFailure"/>.
/// OPEN (skill §15): the non-hardware types are agent proposals, confirm with backend member C.
/// </summary>
public static class AlertTypes
{
    // hardware
    public const string CpuUsage = "CPU_USAGE";
    public const string MemoryUsage = "MEMORY_USAGE";
    public const string HardwareFailure = "HARDWARE_FAILURE";
    public const string TemperatureWarning = "TEMPERATURE_WARNING";

    // anti_theft: a watched USB peripheral was removed and not reconnected within the debounce window.
    public const string DeviceRemoved = "DEVICE_REMOVED";

    // security_violation
    /// <summary>The lock screen helper is not running while the station should be locked.</summary>
    public const string LockScreenMissing = "LOCK_SCREEN_MISSING";

    /// <summary>Someone else owns the lock-screen pipe, or an untrusted process tried to use it.</summary>
    public const string IpcTampering = "IPC_TAMPERING";
}
