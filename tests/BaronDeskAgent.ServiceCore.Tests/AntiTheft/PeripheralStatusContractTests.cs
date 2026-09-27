using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.AntiTheft;

namespace BaronDeskAgent.ServiceCore.Tests.AntiTheft;

public sealed class PeripheralStatusContractTests
{
    private static readonly PeripheralState Mouse = new()
    {
        DeviceId = @"USB\VID_046D&PID_C077\5&1A2B3C4D&0&2",
        Name = "USB Input Device",
        VendorProductId = "VID_046D&PID_C077",
        Connected = false,
        ChangedAt = new DateTimeOffset(2026, 9, 27, 20, 15, 0, TimeSpan.Zero)
    };

    [Fact]
    public void Peripheral_status_is_camel_case_on_the_wire()
    {
        var json = JsonSerializer.Serialize(new PeripheralStatusPayload { Peripherals = [Mouse] }, AgentJsonContext.Default.PeripheralStatusPayload);
        var item = JsonDocument.Parse(json).RootElement.GetProperty("peripherals")[0];

        Assert.Equal("VID_046D&PID_C077", item.GetProperty("vendorProductId").GetString());
        Assert.False(item.GetProperty("connected").GetBoolean());
        Assert.Equal(Mouse.DeviceId, item.GetProperty("deviceId").GetString());
        Assert.Equal(Mouse.ChangedAt, item.GetProperty("changedAt").GetDateTimeOffset());
    }

    [Fact]
    public void State_report_always_carries_a_peripheral_list()
    {
        var json = JsonSerializer.Serialize(new StateReportPayload { Locked = true }, AgentJsonContext.Default.StateReportPayload);

        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(json).RootElement.GetProperty("peripherals").ValueKind);
    }

    [Fact]
    public void Registry_starts_empty_and_returns_the_latest_snapshot()
    {
        var registry = new PeripheralRegistry();
        Assert.Empty(registry.Current);

        registry.Set([Mouse]);

        Assert.Same(Mouse, Assert.Single(registry.Current));
    }
}
