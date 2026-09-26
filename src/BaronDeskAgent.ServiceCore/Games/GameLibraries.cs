using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Snapshot of the game launchers installed on this machine, taken once per catalog sync or launch.
/// </summary>
/// <param name="SteamExecutable">Full path of <c>steam.exe</c>, or null when Steam is not installed.</param>
/// <param name="SteamLibraryDirectories">Steam library roots (each holds <c>steamapps\appmanifest_*.acf</c>).</param>
/// <param name="EpicLauncherExecutable">Full path of <c>EpicGamesLauncher.exe</c>, or null when it is not installed.</param>
/// <param name="EpicInstallLocations">Installed Epic games: <c>AppName → install folder</c> (case-insensitive).</param>
public sealed record GameLibraries(
    string? SteamExecutable,
    IReadOnlyList<string> SteamLibraryDirectories,
    string? EpicLauncherExecutable,
    IReadOnlyDictionary<string, string> EpicInstallLocations)
{
    public static GameLibraries None { get; } =
        new(null, [], null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public interface IGameLibraryLocator
{
    GameLibraries Discover();
}

/// <summary>
/// Finds Steam and the Epic Games Launcher from machine-wide locations only (HKLM, Program Files, ProgramData), so the
/// executables the agent starts for launcher games are the ones an administrator installed.
/// </summary>
public sealed partial class WindowsGameLibraryLocator : IGameLibraryLocator
{
    private const int MaxManifestBytes = 1024 * 1024;

    private readonly ILogger<WindowsGameLibraryLocator> _logger;

    public WindowsGameLibraryLocator(ILogger<WindowsGameLibraryLocator> logger)
    {
        _logger = logger;
    }

    private static string EpicManifestDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public GameLibraries Discover()
    {
        var steamDirectory = FindSteamDirectory();
        var steamExecutable = steamDirectory is null ? null : Path.Combine(steamDirectory, "steam.exe");
        if (steamExecutable is not null && !File.Exists(steamExecutable))
        {
            steamExecutable = null;
        }

        return new GameLibraries(
            steamExecutable,
            steamExecutable is null ? [] : ReadSteamLibraries(steamDirectory!, _logger),
            FindEpicLauncher(),
            ReadEpicManifests(EpicManifestDirectory, _logger));
    }

    /// <summary>The Steam install folder plus every library listed in its <c>libraryfolders.vdf</c>.</summary>
    internal static IReadOnlyList<string> ReadSteamLibraries(string steamDirectory, ILogger logger)
    {
        var libraries = new List<string> { steamDirectory };
        var vdf = Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (var path in ParseSteamLibraryPaths(File.ReadAllText(vdf)))
                {
                    if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        libraries.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the Steam library list.");
        }

        return libraries;
    }

    /// <summary>The <c>"path"</c> values of a <c>libraryfolders.vdf</c> (KeyValues text; backslashes are escaped).</summary>
    internal static IEnumerable<string> ParseSteamLibraryPaths(string vdf)
    {
        foreach (Match match in VdfPathRegex().Matches(vdf))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\").Replace("\\\"", "\"");
            if (Path.IsPathFullyQualified(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>Installed Epic games from the launcher's <c>*.item</c> manifests: <c>AppName → InstallLocation</c>.</summary>
    internal static IReadOnlyDictionary<string, string> ReadEpicManifests(string directory, ILogger logger)
    {
        var installs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return installs;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.item"))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxManifestBytes)
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("AppName", out var appName) && appName.ValueKind == JsonValueKind.String &&
                        root.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(appName.GetString()) && !string.IsNullOrWhiteSpace(location.GetString()))
                    {
                        installs.TryAdd(appName.GetString()!, location.GetString()!);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Skipped unreadable Epic manifest {File}.", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list the Epic Games manifests.");
        }

        return installs;
    }

    /// <summary>The executable of a shell <c>open\command</c> value, e.g. <c>"C:\x\app.exe" %1</c> → <c>C:\x\app.exe</c>.</summary>
    internal static string? ParseCommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        string executable;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            executable = command[1..end];
        }
        else
        {
            var space = command.IndexOf(' ');
            executable = space < 0 ? command : command[..space];
        }

        return Path.IsPathFullyQualified(executable) ? executable : null;
    }

    private string? FindSteamDirectory()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = machine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path && Path.IsPathFullyQualified(path) && Directory.Exists(path))
            {
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not read the Steam install path from the registry.");
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private string? FindEpicLauncher()
    {
        try
        {
            // HKLM only: the per-user HKCR view could point the agent at a program any gamer registered.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\Classes\com.epicgames.launcher\shell\open\command");
            if (ParseCommandExecutable(key?.GetValue(null) as string) is { } executable && File.Exists(executable))
            {
                return executable;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not read the Epic Games Launcher location from the registry.");
        }

        var binaries = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Epic Games", "Launcher", "Portal", "Binaries");

        return new[] { "Win64", "Win32" }
            .Select(platform => Path.Combine(binaries, platform, "EpicGamesLauncher.exe"))
            .FirstOrDefault(File.Exists);
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VdfPathRegex();
}
