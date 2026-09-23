using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Tests.Connection;

public sealed class ReconnectBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, 1.0, 2.0)]
    [InlineData(2, 2.0, 4.0)]
    [InlineData(3, 4.0, 8.0)]
    [InlineData(10, 15.0, 30.0)]
    [InlineData(1000, 15.0, 30.0)]
    public void Delay_grows_exponentially_with_jitter_and_stays_capped(int attempt, double min, double max)
    {
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            var delay = ReconnectBackoff.Compute(attempt, Base, Max, random).TotalSeconds;
            Assert.InRange(delay, min, max);
        }
    }
}
