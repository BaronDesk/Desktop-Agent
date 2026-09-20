namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Payload received from the server in response to an enrollment request.
/// Contains the enrollment verdict and, on success, the station JWT credential.
/// </summary>
public sealed record EnrollmentResponse
{
    /// <summary>
    /// Server-assigned station identifier. Only populated when Status is ENROLLED.
    /// </summary>
    public Guid? StationId { get; init; }

    /// <summary>
    /// Long-lived station JWT for WebSocket upgrade authentication.
    /// Only populated when Status is ENROLLED.
    /// </summary>
    public string? StationJwt { get; init; }

    /// <summary>
    /// Enrollment verdict: "ENROLLED", "PENDING", or "REJECTED".
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Optional human-readable message from the server (e.g. rejection reason).
    /// </summary>
    public string? Message { get; init; }
}
