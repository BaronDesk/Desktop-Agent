using System.Text.Json;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public sealed class LoggingTelemetryTransport : ITelemetryTransport
{
    private readonly ILogger<LoggingTelemetryTransport> _logger;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    public LoggingTelemetryTransport(
        ILogger<LoggingTelemetryTransport> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            envelope,
            JsonOptions);

        _logger.LogInformation(
            "Telemetry sent: Type={Type}, Sequence={Sequence}, Id={Id}, Payload={Payload}",
            envelope.Type,
            envelope.Seq,
            envelope.Id,
            json);

        return Task.CompletedTask;
    }
}