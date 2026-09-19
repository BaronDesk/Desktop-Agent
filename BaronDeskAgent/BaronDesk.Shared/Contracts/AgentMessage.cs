using System;

namespace BaronDesk.Shared.Contracts
{
    public class AgentMessage
    {
        public MessageType Type { get; set; }
        public string StationId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public object? Payload { get; set; } // TelemetryPayload, CommandRequest, etc. depending on Type
    }
}