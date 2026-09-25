using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Telemetry.Outbox;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Sends telemetry and alerts upstream.
/// </summary>
/// <remarks>
/// Live telemetry is cache-only on the backend (latest value, 30 s TTL), so it is sent when online and
/// dropped otherwise. Alerts are persisted: they always go through the SQLite outbox, which survives outages
/// and restarts and delivers them in order.
/// </remarks>
public sealed class TelemetryPublisher
{
    private readonly IServerConnection _connection;
    private readonly OutboxQueue _outbox;
    private readonly ILogger<TelemetryPublisher> _logger;

    public TelemetryPublisher(IServerConnection connection, OutboxQueue outbox, ILogger<TelemetryPublisher> logger)
    {
        _connection = connection;
        _outbox = outbox;
        _logger = logger;
    }

    public bool IsOnline => _connection.IsReady;

    public async Task PublishTelemetryAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken)
    {
        if (!_connection.IsReady)
        {
            return;
        }

        try
        {
            await _connection.SendAsync(
                MessageTypes.Telemetry,
                new TelemetryPayload { Samples = samples },
                AgentJsonContext.Default.TelemetryPayload,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Dropped a telemetry frame.");
        }
    }

    public Task PublishAlertAsync(AlertPayload alert, CancellationToken cancellationToken) =>
        _outbox.EnqueueAsync(MessageTypes.Alert, JsonSerializer.Serialize(alert, AgentJsonContext.Default.AlertPayload), cancellationToken);
}
