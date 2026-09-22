namespace BaronDeskAgent.ServiceCore.Connection;

internal static class ReconnectBackoff
{
    /// <summary>
    /// Exponential backoff with "equal jitter": half of the capped delay is fixed, half is random,
    /// so a venue-wide server restart does not make every station reconnect at the same instant.
    /// </summary>
    /// <param name="attempt">1 for the first retry.</param>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random random)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var capped = Math.Min(baseDelay.TotalSeconds * Math.Pow(2, exponent), maxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(capped / 2 + random.NextDouble() * capped / 2);
    }
}
