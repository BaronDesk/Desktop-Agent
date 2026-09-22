namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Cached catalog entry: <c>gameId → executable</c>. <c>LAUNCH_GAME</c> only ever runs entries from this
/// catalog, never a path taken from a command payload.
/// </summary>
public sealed class GameCatalogEntity
{
    public required string GameId { get; init; }

    public required string Name { get; init; }

    public required string ExecutablePath { get; init; }

    public string? LaunchArguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
