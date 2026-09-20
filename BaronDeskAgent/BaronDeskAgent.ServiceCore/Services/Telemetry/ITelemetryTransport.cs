using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public interface ITelemetryTransport
{
    Task SendAsync(
        Envelope envelope,
        CancellationToken cancellationToken);
}