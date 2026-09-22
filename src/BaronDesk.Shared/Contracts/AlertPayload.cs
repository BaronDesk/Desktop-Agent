namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>alert</c> payload, aligned with the Prisma <c>TelemetryAlert</c> model plus the V4 <c>category</c>.
/// </summary>
public sealed record AlertPayload
{
    /// <summary>One of <see cref="AlertCategories"/>.</summary>
    public required string Category { get; init; }

    /// <summary>One of <see cref="AlertTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>One of <see cref="AlertSeverities"/>.</summary>
    public required string Severity { get; init; }

    public required string Detail { get; init; }

    /// <summary>When the condition happened, on the (estimated) server clock.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}
