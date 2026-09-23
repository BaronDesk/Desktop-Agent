using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Tests.Policy;

public sealed class StationPolicyTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(new StationPolicy().Validate());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.5)]
    [InlineData(301)]
    public void Out_of_bounds_and_non_finite_values_are_rejected(double cadence)
    {
        Assert.NotEmpty(new StationPolicy { TelemetryCadenceSeconds = cadence }.Validate());
    }

    [Fact]
    public void Heartbeat_must_fit_twice_inside_the_lease()
    {
        Assert.NotEmpty(new StationPolicy { HeartbeatIntervalSeconds = 40, DefaultLeaseDurationSeconds = 60 }.Validate());
        Assert.Empty(new StationPolicy { HeartbeatIntervalSeconds = 30, DefaultLeaseDurationSeconds = 60 }.Validate());
    }

    [Fact]
    public void Apply_changes_only_the_fields_present()
    {
        var updatedAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var original = new StationPolicy();

        var updated = original.Apply(new PolicyUpdatePayload { CpuTempAlertThreshold = 90, StopGameOnLock = false }, updatedAt);

        Assert.Equal(90, updated.CpuTempAlertThreshold);
        Assert.False(updated.StopGameOnLock);
        Assert.Equal(original.HeartbeatIntervalSeconds, updated.HeartbeatIntervalSeconds);
        Assert.Equal(updatedAt, updated.UpdatedAt);
        Assert.Equal(85, original.CpuTempAlertThreshold);
    }
}
