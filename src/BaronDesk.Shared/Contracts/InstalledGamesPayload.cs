namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>installed_games</c> payload: launcher games found installed on this station, sent after every catalog sync,
/// so an admin can add the missing ones to the catalog instead of typing app ids by hand.
/// OPEN (skill §15 item 9): agent proposal, confirm with backend member C.
/// </summary>
/// <remarks>
/// Suggestions only: the agent never launches anything from this list. <c>LAUNCH_GAME</c> still runs catalog
/// entries only. Plain <c>.exe</c> games cannot be discovered reliably and are not listed.
/// </remarks>
public sealed record InstalledGamesPayload
{
    public required IReadOnlyList<DiscoveredGame> Games { get; init; }
}

public sealed record DiscoveredGame
{
    /// <summary><see cref="GameLaunchTypes.Steam"/> or <see cref="GameLaunchTypes.Epic"/>.</summary>
    public required string LaunchType { get; init; }

    /// <summary>Steam app id, or Epic <c>AppName</c>: what a catalog entry's <c>target</c> would be.</summary>
    public required string Target { get; init; }

    public required string Name { get; init; }

    /// <summary>Suggested image name (e.g. <c>FortniteLauncher.exe</c>), when the launcher manifest names one.</summary>
    public string? ProcessName { get; init; }

    /// <summary>True when a catalog entry already points at this game.</summary>
    public required bool InCatalog { get; init; }
}
