using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.AntiTheft;

/// <summary>
/// Latest peripheral snapshot, written by <see cref="UsbMonitorService"/>'s reader loop and read by the
/// connection worker for <c>state_report</c>. The list is immutable, so a plain volatile swap is enough.
/// </summary>
public sealed class PeripheralRegistry
{
    private volatile IReadOnlyList<PeripheralState> _current = [];

    public IReadOnlyList<PeripheralState> Current => _current;

    internal void Set(IReadOnlyList<PeripheralState> peripherals) => _current = peripherals;
}
