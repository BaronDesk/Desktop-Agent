using System.Collections.Frozen;

namespace BaronDesk.Shared.Contracts;

// OPEN (skill §15 item 9): catalog delivery is an agent-side proposal, confirm with backend member C.
//   server → agent  CATALOG_UPDATE {}                          "your catalog changed" (also fine to send on every connect)
//   agent → server  GET /stations/me/games (station JWT)       → GameCatalogResponse, already resolved for this machine
//   agent → server  catalog_status                             which entries this station can actually launch

/// <summary>
/// How a catalog entry is started. The agent builds the command line itself; the backend never sends one.
/// </summary>
public static class GameLaunchTypes
{
    /// <summary><c>target</c> is the fully qualified path of the game's <c>.exe</c>.</summary>
    public const string Exe = "exe";

    /// <summary><c>target</c> is the Steam app id (digits), started with <c>steam.exe -applaunch</c>.</summary>
    public const string Steam = "steam";

    /// <summary><c>target</c> is the Epic Games Launcher <c>AppName</c>, started through the launcher.</summary>
    public const string Epic = "epic";

    public static readonly FrozenSet<string> All = new[] { Exe, Steam, Epic }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// One game in this station's catalog, as the backend resolved it for this machine (per-machine overrides applied).
/// Every field is validated by the agent; an invalid entry is skipped and reported, it never blocks the others.
/// </summary>
public sealed record CatalogGame
{
    public string? GameId { get; init; }

    public string? Name { get; init; }

    /// <summary>One of <see cref="GameLaunchTypes"/>, case-insensitive. Defaults to <c>exe</c>.</summary>
    public string? LaunchType { get; init; }

    /// <summary>Executable path, Steam app id or Epic AppName, depending on <see cref="LaunchType"/>.</summary>
    public string? Target { get; init; }

    /// <summary>Extra command-line arguments (<c>exe</c> and <c>steam</c> only).</summary>
    public string? Arguments { get; init; }

    /// <summary><c>exe</c> only. Defaults to the executable's folder.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Image name of the game's own process (e.g. <c>cs2.exe</c>). Lets the agent see and close games started through
    /// a launcher (Steam, Epic) or a bootstrapper that exits. Strongly recommended for <c>steam</c> and <c>epic</c>.
    /// </summary>
    public string? ProcessName { get; init; }
}

/// <summary>
/// Body of <c>GET /stations/me/games</c>: the full catalog. Entries not listed are removed from the station.
/// </summary>
public sealed record GameCatalogResponse
{
    public IReadOnlyList<CatalogGame?>? Games { get; init; }
}

/// <summary>
/// <c>catalog_status</c> payload, sent after every catalog sync (so on every connect too).
/// </summary>
public sealed record CatalogStatusPayload
{
    public required IReadOnlyList<CatalogGameStatus> Games { get; init; }
}

public sealed record CatalogGameStatus
{
    public required string GameId { get; init; }

    /// <summary>True when the game can be launched on this station right now.</summary>
    public required bool Installed { get; init; }

    /// <summary>Why the game cannot be launched (never contains local file paths).</summary>
    public string? Reason { get; init; }
}
