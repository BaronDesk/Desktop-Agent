using System;
using System.Collections.Generic;
using System.Text;

namespace BaronDesk.Shared.Contracts
{
    // Categories of message exchanged on the SOCKET channel
    // (Service Core <-> Real-time Hub)
    public enum MessageType
    {
        Register,
        Heartbeat,
        Telemetry,
        Command,
        Ack,
        Nack,
        Alert,
        ConnectionAccepted,
        ConnectionRejected
    }
}
