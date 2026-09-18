namespace BaronDesk.Shared.Models;

public class TelemetryEnvelope
{
    public string Type { get; set; } = string.Empty;

    public Guid Id { get; set; }

    public DateTime Ts { get; set; }

    public long Seq { get; set; }

    public object Payload { get; set; } = new();
}