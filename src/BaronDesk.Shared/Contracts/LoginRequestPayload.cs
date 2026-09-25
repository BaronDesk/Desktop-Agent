namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>login_request</c> payload: a credential typed on the lock screen, relayed to the backend for verification.
/// OPEN (skill §15 item 2), confirm with backend member C.
/// </summary>
/// <remarks>
/// The agent never verifies credentials. It relays them over the authenticated station channel and never
/// stores or logs them; the backend answers with <c>login_result</c> and, if accepted, <c>UNLOCK</c>.
/// </remarks>
public sealed record LoginRequestPayload
{
    /// <summary>Login method, e.g. <c>"pin"</c>. QR-from-phone is planned (skill §15 item 11).</summary>
    public required string Method { get; init; }

    public required string Credential { get; init; }

    public override string ToString() => $"LoginRequestPayload {{ Method = {Method}, Credential = *** }}";
}
