using System;

namespace BaronDesk.Shared.Contracts
{
    public class CommandResponse
    {
        public required Guid CommandId { get; set; }
        public required bool Success { get; set; }
        public string? Error { get; set; } // null when Success = true
        public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
    }
}