using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Session;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Session;

public sealed class LeaseManagerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly LeaseManager _lease;

    public LeaseManagerTests()
    {
        var policy = new FakePolicyStore(new StationPolicy { DefaultLeaseDurationSeconds = 60, LeaseGracePeriodSeconds = 10 });
        _lease = new LeaseManager(policy, new ServerClock(_time), _time, NullLogger<LeaseManager>.Instance);
    }

    [Fact]
    public void Walks_through_valid_grace_and_expired()
    {
        Assert.Equal(LeaseStatus.None, _lease.GetStatus());

        _lease.Grant(TimeSpan.FromSeconds(30));
        Assert.Equal(LeaseStatus.Valid, _lease.GetStatus());

        _time.Advance(TimeSpan.FromSeconds(35));
        Assert.Equal(LeaseStatus.InGrace, _lease.GetStatus());

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(LeaseStatus.Expired, _lease.GetStatus());

        _lease.Revoke();
        Assert.Equal(LeaseStatus.None, _lease.GetStatus());
    }

    [Fact]
    public void Caps_any_lease_at_the_policy_duration()
    {
        _lease.Grant(TimeSpan.FromDays(1));

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(LeaseStatus.InGrace, _lease.GetStatus());
    }

    [Fact]
    public void Missing_terms_mean_the_policy_default()
    {
        _lease.Grant(null);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(LeaseStatus.Valid, _lease.GetStatus());
    }

    [Fact]
    public void Duration_uses_server_clock_values_only()
    {
        var serverTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(60), LeaseManager.ResolveDuration(null, serverTime.AddSeconds(60), serverTime));
        Assert.Equal(TimeSpan.FromSeconds(45), LeaseManager.ResolveDuration(45, serverTime.AddSeconds(60), serverTime));
        Assert.Null(LeaseManager.ResolveDuration(null, null, serverTime));
    }

    [Fact]
    public void An_expiry_in_the_past_resolves_to_a_non_positive_duration()
    {
        var serverTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(LeaseManager.ResolveDuration(null, serverTime.AddSeconds(-5), serverTime) <= TimeSpan.Zero);
        Assert.True(LeaseManager.ResolveDuration(double.NaN, null, serverTime) <= TimeSpan.Zero);
    }
}
