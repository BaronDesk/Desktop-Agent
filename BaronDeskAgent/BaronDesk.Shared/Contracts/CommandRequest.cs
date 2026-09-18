using System;

namespace BaronDesk.Shared.Contracts
{
    public class CommandRequest
    {
        public Guid CommandId { get; set; } = Guid.NewGuid();
        public CommandType Command { get; set; }
        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    }
}