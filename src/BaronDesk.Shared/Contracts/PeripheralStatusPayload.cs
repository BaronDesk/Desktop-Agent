namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>peripheral_status</c> payload: the full list of watched venue peripherals, sent whenever one of them
/// disconnects or comes back. The same list rides on every <c>state_report</c>.
/// OPEN (skill §15): not in the frozen list, agent proposal, confirm with backend member C.
/// </summary>
/// <remarks>
/// A full snapshot (not a delta) so a lost frame never leaves the dashboard wrong: the next one, or the next
/// reconnect, repairs it. A peripheral seen connected again lets the backend resolve its <c>DEVICE_REMOVED</c> alert.
/// </remarks>
public sealed record PeripheralStatusPayload
{
    public required IReadOnlyList<PeripheralState> Peripherals { get; init; }
}

/// <summary>One watched wired USB HID device (keyboard, mouse, headset controls…).</summary>
public sealed record PeripheralState
{
    /// <summary>Stable id of the physical device (PnP parent path); every interface of a composite device shares it.</summary>
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    /// <summary>e.g. <c>VID_046D&amp;PID_C077</c>: identifies the model, not the unit.</summary>
    public required string VendorProductId { get; init; }

    public required bool Connected { get; init; }

    /// <summary>When <see cref="Connected"/> last changed (or when the device was first seen), on the estimated server clock.</summary>
    public required DateTimeOffset ChangedAt { get; init; }
}
