using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Connection;

public sealed class ReplayGuardTests
{
    private static readonly DateTimeOffset LocalNow = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(LocalNow);
    private readonly ServerClock _serverClock;
    private readonly ReplayGuard _guard;

    public ReplayGuardTests()
    {
        _serverClock = new ServerClock(_time);
        _guard = new ReplayGuard(_serverClock, Options.Create(new AgentOptions { MaxTimestampDriftSeconds = 60 }));
    }

    [Fact]
    public void Rejects_a_sequence_that_does_not_increase()
    {
        Assert.True(_guard.Validate(5, LocalNow).IsValid);
        Assert.False(_guard.Validate(5, LocalNow).IsValid);
        Assert.False(_guard.Validate(4, LocalNow).IsValid);
        Assert.True(_guard.Validate(6, LocalNow).IsValid);
    }

    [Fact]
    public void Reset_starts_a_new_sequence_scope()
    {
        Assert.True(_guard.Validate(10, LocalNow).IsValid);
        _guard.Reset();
        Assert.True(_guard.Validate(1, LocalNow).IsValid);
    }

    [Fact]
    public void Rejects_stale_timestamps_without_consuming_the_sequence()
    {
        Assert.False(_guard.Validate(1, LocalNow.AddMinutes(-5)).IsValid);
        Assert.True(_guard.Validate(1, LocalNow).IsValid);
    }

    [Fact]
    public void Freshness_is_judged_against_the_server_clock_not_the_drifted_local_clock()
    {
        // The station clock is 10 minutes behind the server (no NTP at the venue).
        var serverNow = LocalNow.AddMinutes(10);
        Assert.False(_guard.Validate(1, serverNow).IsValid);

        _serverClock.Synchronize(serverNow);

        Assert.True(_guard.Validate(2, serverNow.AddSeconds(1)).IsValid);
    }

    [Fact]
    public void Restrictive_commands_skip_freshness_but_not_the_sequence_check()
    {
        Assert.True(_guard.Validate(1, LocalNow.AddHours(-3), enforceFreshness: false).IsValid);
        Assert.False(_guard.Validate(1, LocalNow, enforceFreshness: false).IsValid);
    }
}
