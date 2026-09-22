using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Session;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Session;

public sealed class StationControllerTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLockScreen _lockScreen = new();
    private readonly FakePolicyStore _policy = new(new StationPolicy { DefaultLeaseDurationSeconds = 60, LeaseGracePeriodSeconds = 10 });
    private readonly SessionService _sessions = new(NullLogger<SessionService>.Instance);
    private TestDatabase _database = null!;
    private StationController _station = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();

        var lease = new LeaseManager(_policy, new ServerClock(_time), _time, NullLogger<LeaseManager>.Instance);
        var games = new GameService(
            new GameCatalogRepository(_database.Database),
            new InteractiveProcessLauncher(NullLogger<InteractiveProcessLauncher>.Instance),
            NullLogger<GameService>.Instance);

        _station = new StationController(
            new LockService(_lockScreen, NullLogger<LockService>.Instance),
            _sessions,
            lease,
            games,
            _policy,
            _time,
            NullLogger<StationController>.Instance);
    }

    public async Task DisposeAsync()
    {
        _station.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public void Starts_locked()
    {
        Assert.True(_station.GetSnapshot().Locked);
        Assert.Null(_station.GetSnapshot().SessionId);
    }

    [Fact]
    public async Task Unlock_binds_the_session_and_hides_the_overlay()
    {
        var sessionId = Guid.NewGuid();

        await _station.StartSessionAsync(sessionId, TimeSpan.FromSeconds(60), CancellationToken.None);

        var snapshot = _station.GetSnapshot();
        Assert.False(snapshot.Locked);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.NotNull(snapshot.LeaseExpiresAt);
        Assert.False(_lockScreen.IsShown);
    }

    [Fact]
    public async Task Fails_closed_when_the_lease_runs_out_offline()
    {
        await _station.StartSessionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), CancellationToken.None);

        AdvanceInSteps(TimeSpan.FromSeconds(35)); // inside the 10 s grace period
        Assert.False(_station.GetSnapshot().Locked);

        AdvanceInSteps(TimeSpan.FromSeconds(10)); // past the grace period
        await WaitUntilAsync(() => _station.GetSnapshot().Locked);

        Assert.True(_lockScreen.IsShown);
        Assert.Null(_station.GetSnapshot().SessionId);
    }

    [Fact]
    public async Task Heartbeat_renewals_keep_the_station_unlocked()
    {
        await _station.StartSessionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            AdvanceInSteps(TimeSpan.FromSeconds(20));
            _station.RenewLease(TimeSpan.FromSeconds(30));
        }

        Assert.False(_station.GetSnapshot().Locked);
    }

    [Fact]
    public async Task A_renewal_reporting_an_expired_lease_is_not_honoured()
    {
        await _station.StartSessionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), CancellationToken.None);

        AdvanceInSteps(TimeSpan.FromSeconds(20));
        _station.RenewLease(TimeSpan.FromSeconds(-5));
        AdvanceInSteps(TimeSpan.FromSeconds(25));

        await WaitUntilAsync(() => _station.GetSnapshot().Locked);
    }

    [Fact]
    public async Task Lock_revokes_the_lease_but_keeps_the_session_bound()
    {
        var sessionId = Guid.NewGuid();
        await _station.StartSessionAsync(sessionId, null, CancellationToken.None);

        Assert.True(await _station.LockAsync(CancellationToken.None));

        var snapshot = _station.GetSnapshot();
        Assert.True(snapshot.Locked);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Null(snapshot.LeaseExpiresAt);
    }

    [Fact]
    public async Task Lock_reports_failure_when_the_overlay_is_not_confirmed()
    {
        _lockScreen.Confirms = false;

        Assert.False(await _station.LockAsync(CancellationToken.None));
        Assert.True(_station.GetSnapshot().Locked);
    }

    [Fact]
    public async Task End_session_for_another_session_is_ignored()
    {
        var active = Guid.NewGuid();
        await _station.StartSessionAsync(active, null, CancellationToken.None);

        var result = await _station.EndSessionAsync(Guid.NewGuid(), "late redelivery", CancellationToken.None);

        Assert.Equal(EndSessionResult.StaleSessionIgnored, result);
        Assert.False(_station.GetSnapshot().Locked);
        Assert.Equal(active, _station.GetSnapshot().SessionId);
    }

    [Fact]
    public async Task End_session_locks_and_forgets_the_session()
    {
        var active = Guid.NewGuid();
        await _station.StartSessionAsync(active, null, CancellationToken.None);

        var result = await _station.EndSessionAsync(active, "user_logout", CancellationToken.None);

        Assert.Equal(EndSessionResult.Ended, result);
        Assert.True(_station.GetSnapshot().Locked);
        Assert.Null(_station.GetSnapshot().SessionId);
        Assert.Null(_station.GetSnapshot().LeaseExpiresAt);
    }

    [Fact]
    public async Task A_new_unlock_replaces_the_previous_session()
    {
        await _station.StartSessionAsync(Guid.NewGuid(), null, CancellationToken.None);
        var next = Guid.NewGuid();

        await _station.StartSessionAsync(next, null, CancellationToken.None);

        Assert.Equal(next, _sessions.CurrentSessionId);
    }

    [Fact]
    public void Lease_renewal_without_a_session_is_ignored()
    {
        _station.RenewLease(TimeSpan.FromSeconds(60));

        Assert.Null(_station.GetSnapshot().LeaseExpiresAt);
    }

    private void AdvanceInSteps(TimeSpan total)
    {
        var step = TimeSpan.FromSeconds(1);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            _time.Advance(step);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
