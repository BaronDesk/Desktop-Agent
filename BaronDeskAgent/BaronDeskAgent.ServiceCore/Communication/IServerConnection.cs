using System.Text.Json;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Communication;

public interface IServerConnection
{
    /// <summary>
    /// Indicates whether the WebSocket connection is open and ready.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Generates the next monotonically increasing sequence number for this connection session.
    /// </summary>
    long NextSequence();

    /// <summary>
    /// Resets the sequence counter to zero upon new connection establishment.
    /// </summary>
    void ResetSequence();

    /// <summary>
    /// Connects to the server WebSocket endpoint with pinned certificate validation.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cleanly closes and disposes the current WebSocket connection.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends an envelope to the server over the WebSocket.
    /// Thread-safe: serializes outbound frame transmissions.
    /// </summary>
    Task SendAsync(Envelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a strongly-typed envelope to the server over the WebSocket.
    /// Thread-safe: serializes outbound frame transmissions.
    /// </summary>
    Task SendAsync<T>(Envelope<T> envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Asynchronously receives the next JSON envelope frame from the server.
    /// Returns null if the connection was closed by the server or terminated.
    /// </summary>
    Task<Envelope<JsonElement>?> ReceiveAsync(CancellationToken cancellationToken);
}
