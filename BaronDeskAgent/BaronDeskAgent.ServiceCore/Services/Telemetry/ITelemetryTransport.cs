using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public interface ITelemetryTransport
{
    Task SendAsync(
        TelemetryEnvelope envelope,
        CancellationToken cancellationToken);
}