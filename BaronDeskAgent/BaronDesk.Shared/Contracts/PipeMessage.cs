using System;

namespace BaronDesk.Shared.Contracts
{
    public enum PipeMessageKind
    {
        ShowLock,      // Service Core -> Lock-UI Helper
        HideLock,      // Service Core -> Lock-UI Helper
        LockShown,     // Lock-UI Helper -> Service Core
        LockHidden,    // Lock-UI Helper -> Service Core
        HelperAlive    // Lock-UI Helper -> Service Core (periodic ping)
    }

    public class PipeMessage
    {
        public PipeMessageKind Kind { get; set; }
        public Guid? CommandId { get; set; } // ties back to the CommandRequest that triggered this, if any
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}