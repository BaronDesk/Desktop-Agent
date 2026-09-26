using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Cached catalog entry: <c>gameId → how to start it on this machine</c>. <c>LAUNCH_GAME</c> only ever runs entries
/// from this catalog, never a path taken from a command payload.
/// </summary>
public sealed class GameCatalogEntity
{
    public required string GameId { get; init; }

    public required string Name { get; init; }

    /// <summary>One of <see cref="GameLaunchTypes"/>.</summary>
    public string LaunchType { get; init; } = GameLaunchTypes.Exe;

    /// <summary>Executable path (<c>exe</c>), Steam app id (<c>steam</c>) or Epic AppName (<c>epic</c>).</summary>
    public required string Target { get; init; }

    public string? LaunchArguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Image name of the game's process without <c>.exe</c>, used to find and close it.</summary>
    public string? ProcessName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
