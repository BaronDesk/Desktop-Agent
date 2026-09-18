using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services;

public interface ITelemetryTransport
{
    Task SendAsync(
        TelemetryEnvelope envelope,
        CancellationToken cancellationToken);
}