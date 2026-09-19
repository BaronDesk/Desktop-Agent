using System;

namespace BaronDesk.Shared.Contracts
{
    public class CommandResponse
    {
        public Guid CommandId { get; set; }
        public bool Success { get; set; }
        public string? Reason { get; set; } // null when Success = true
        public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
    }
}