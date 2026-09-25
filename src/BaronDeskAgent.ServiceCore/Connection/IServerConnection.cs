using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// The single WSS link to the venue backend (<c>/agent-ws</c>).
/// </summary>
public interface IServerConnection
{
    /// <summary>
    /// True once the socket is open and the handshake + state report were sent. Heartbeats, telemetry,
    /// outbox traffic and logins wait for this, so nothing reaches the backend before the handshake.
    /// </summary>
    bool IsReady { get; }

    /// <summary>Raised with the new value whenever <see cref="IsReady"/> changes.</summary>
    event Action<bool>? ReadyChanged;

    Task WaitUntilReadyAsync(CancellationToken cancellationToken);

    /// <summary>Opens the socket (pinned TLS, station credential on the upgrade request).</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Called by the connection worker once the handshake and state report are on the wire.</summary>
    void MarkReady();

    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends one envelope. <c>seq</c> and <c>ts</c> are stamped under the send lock, so frames reach the wire
    /// in sequence order no matter which component sends them.
    /// </summary>
    /// <param name="messageId">Envelope id; pass the original id when retrying so the backend can deduplicate.</param>
    /// <returns>The envelope id.</returns>
    Task<Guid> SendAsync<T>(
        string type,
        T payload,
        JsonTypeInfo<T> payloadTypeInfo,
        CancellationToken cancellationToken,
        Guid? messageId = null);

    Task<ReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}

public enum ReceiveStatus
{
    Message,

    /// <summary>A frame that is not a valid envelope. The connection stays open; the frame is ignored.</summary>
    Malformed,

    Closed
}

public readonly record struct ReceiveResult(ReceiveStatus Status, Envelope<JsonElement>? Envelope, string? Error)
{
    public static ReceiveResult Closed { get; } = new(ReceiveStatus.Closed, null, null);

    public static ReceiveResult Message(Envelope<JsonElement> envelope) => new(ReceiveStatus.Message, envelope, null);

    public static ReceiveResult Malformed(string error) => new(ReceiveStatus.Malformed, null, error);
}
