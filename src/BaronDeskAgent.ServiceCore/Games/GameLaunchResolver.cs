using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>The program the agent starts for a game, in the gamer's session.</summary>
public sealed record LaunchCommand(string ExecutablePath, string? Arguments, string? WorkingDirectory);

/// <summary>Either a <see cref="LaunchCommand"/>, or the reason the game cannot be launched on this station.</summary>
public readonly record struct LaunchResolution(LaunchCommand? Command, string? Reason)
{
    public bool IsInstalled => Command is not null;

    public static LaunchResolution NotInstalled(string reason) => new(null, reason);
}

/// <summary>
/// Decides whether a catalog entry can be launched here and builds its command line. Reasons never contain local
/// paths, since they are reported to the backend.
/// </summary>
public sealed class GameLaunchResolver
{
    private readonly string[] _allowedDirectories;

    public GameLaunchResolver(IOptions<AgentOptions> options)
    {
        _allowedDirectories = (options.Value.AllowedGameDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar)
            .ToArray();
    }

    public LaunchResolution Resolve(GameCatalogEntity game, GameLibraries libraries) => game.LaunchType switch
    {
        GameLaunchTypes.Exe => ResolveExecutable(game),
        GameLaunchTypes.Steam => ResolveSteam(game, libraries),
        GameLaunchTypes.Epic => ResolveEpic(game, libraries),
        _ => LaunchResolution.NotInstalled($"Launch type '{game.LaunchType}' is not supported by this agent.")
    };

    private LaunchResolution ResolveExecutable(GameCatalogEntity game)
    {
        if (!IsInsideAllowedDirectories(game.Target))
        {
            return LaunchResolution.NotInstalled("The executable is outside the station's allowed game folders.");
        }

        if (!File.Exists(game.Target))
        {
            return LaunchResolution.NotInstalled("The executable was not found on this station.");
        }

        return new LaunchResolution(new LaunchCommand(game.Target, game.LaunchArguments, game.WorkingDirectory), null);
    }

    private static LaunchResolution ResolveSteam(GameCatalogEntity game, GameLibraries libraries)
    {
        if (libraries.SteamExecutable is null)
        {
            return LaunchResolution.NotInstalled("Steam is not installed on this station.");
        }

        var manifest = $"appmanifest_{game.Target}.acf";
        if (!libraries.SteamLibraryDirectories.Any(library => File.Exists(Path.Combine(library, "steamapps", manifest))))
        {
            return LaunchResolution.NotInstalled("The game is not installed in any Steam library on this station.");
        }

        var arguments = string.IsNullOrEmpty(game.LaunchArguments)
            ? $"-applaunch {game.Target}"
            : $"-applaunch {game.Target} {game.LaunchArguments}";

        return new LaunchResolution(new LaunchCommand(libraries.SteamExecutable, arguments, null), null);
    }

    private static LaunchResolution ResolveEpic(GameCatalogEntity game, GameLibraries libraries)
    {
        if (libraries.EpicLauncherExecutable is null)
        {
            return LaunchResolution.NotInstalled("The Epic Games Launcher is not installed on this station.");
        }

        if (!libraries.EpicInstallLocations.TryGetValue(game.Target, out var location) || !Directory.Exists(location))
        {
            return LaunchResolution.NotInstalled("The game is not installed in the Epic Games Launcher on this station.");
        }

        // The AppName is validated to [A-Za-z0-9._-], so it needs no escaping inside the quoted URI.
        var uri = $"com.epicgames.launcher://apps/{game.Target}?action=launch&silent=true";
        return new LaunchResolution(new LaunchCommand(libraries.EpicLauncherExecutable, $"\"{uri}\"", null), null);
    }

    private bool IsInsideAllowedDirectories(string path)
    {
        if (_allowedDirectories.Length == 0)
        {
            return true;
        }

        // GetFullPath collapses "..", so D:\Games\..\Windows\x.exe is not inside D:\Games.
        var fullPath = Path.GetFullPath(path);
        return _allowedDirectories.Any(directory => fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }
}
