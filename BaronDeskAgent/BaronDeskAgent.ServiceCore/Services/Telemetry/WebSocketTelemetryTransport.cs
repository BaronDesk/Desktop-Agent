using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Communication;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public sealed class WebSocketTelemetryTransport : ITelemetryTransport
{
    private readonly IServerConnection _serverConnection;
    private readonly ILogger<WebSocketTelemetryTransport> _logger;

    public WebSocketTelemetryTransport(
        IServerConnection serverConnection,
        ILogger<WebSocketTelemetryTransport> logger)
    {
        _serverConnection = serverConnection;
        _logger = logger;
    }

    public async Task SendAsync(
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        if (!_serverConnection.IsConnected)
        {
            throw new InvalidOperationException(
                "WebSocket is not connected. Telemetry buffered for outbox retry.");
        }

        await _serverConnection.SendAsync(envelope, cancellationToken);

        _logger.LogDebug(
            "Outbox envelope transmitted over WebSocket. Type={Type}, Id={Id}, Seq={Seq}",
            envelope.Type,
            envelope.Id,
            envelope.Seq);
    }
}
