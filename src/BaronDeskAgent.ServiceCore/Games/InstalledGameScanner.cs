using System.Text.Json;
using System.Text.RegularExpressions;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>A launcher game found installed on this machine (before it is compared with the catalog).</summary>
public sealed record InstalledGame(string LaunchType, string Target, string Name, string? ProcessName);

public interface IInstalledGameScanner
{
    IReadOnlyList<InstalledGame> Scan(GameLibraries libraries);
}

/// <summary>
/// Lists installed Steam and Epic games from the launchers' own manifests, read-only, to suggest catalog entries.
/// Uses the same machine-wide locations as <see cref="WindowsGameLibraryLocator"/>.
/// </summary>
public sealed partial class InstalledGameScanner : IInstalledGameScanner
{
    public const int MaxGames = 500;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxNameLength = 200;

    /// <summary>StateFlags bit set once Steam has fully installed an app.</summary>
    private const int SteamStateFullyInstalled = 4;

    // Steam installs these next to games; they are not games (redistributables, runtimes, compatibility tools).
    private static readonly HashSet<string> SteamToolAppIds = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100"];
    private static readonly string[] SteamToolPrefixes = ["Steamworks", "Steam Linux Runtime", "Proton", "SteamVR"];

    private readonly ILogger<InstalledGameScanner> _logger;

    public InstalledGameScanner(ILogger<InstalledGameScanner> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<InstalledGame> Scan(GameLibraries libraries) =>
        ScanSteam(libraries.SteamLibraryDirectories, _logger)
            .Concat(ScanEpic(WindowsGameLibraryLocator.EpicManifestDirectory, _logger))
            .GroupBy(game => (game.LaunchType, game.Target.ToUpperInvariant()))
            .Select(group => group.First())
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxGames)
            .ToList();

    /// <summary>Fully installed Steam apps from <c>steamapps\appmanifest_*.acf</c> in every library.</summary>
    internal static IEnumerable<InstalledGame> ScanSteam(IEnumerable<string> libraryDirectories, ILogger logger)
    {
        foreach (var library in libraryDirectories)
        {
            var steamApps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try
            {
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }

                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not list Steam manifests in {Library}.", library);
                continue;
            }

            foreach (var file in manifests)
            {
                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxManifestBytes)
                    {
                        continue;
                    }

                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Skipped unreadable Steam manifest {File}.", Path.GetFileName(file));
                    continue;
                }

                if (ParseSteamManifest(text) is { } game)
                {
                    yield return game;
                }
            }
        }
    }

    /// <summary>One <c>appmanifest_*.acf</c> (KeyValues text): null unless it is a fully installed game.</summary>
    internal static InstalledGame? ParseSteamManifest(string acf)
    {
        string? appId = null, name = null, stateFlags = null;
        foreach (Match match in AcfFieldRegex().Matches(acf))
        {
            var value = match.Groups[2].Value.Replace("\\\"", "\"").Replace(@"\\", @"\");
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "appid": appId ??= value; break;
                case "name": name ??= value; break;
                case "stateflags": stateFlags ??= value; break;
            }
        }

        if (appId is null || !appId.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(name) ||
            !int.TryParse(stateFlags, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var flags) ||
            (flags & SteamStateFullyInstalled) == 0)
        {
            return null;
        }

        if (SteamToolAppIds.Contains(appId) || SteamToolPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new InstalledGame(GameLaunchTypes.Steam, appId, Trim(name), ProcessName: null);
    }

    /// <summary>Complete Epic game installs from the launcher's <c>*.item</c> manifests.</summary>
    internal static IEnumerable<InstalledGame> ScanEpic(string manifestDirectory, ILogger logger)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(manifestDirectory))
            {
                yield break;
            }

            files = Directory.EnumerateFiles(manifestDirectory, "*.item").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not list the Epic Games manifests.");
            yield break;
        }

        foreach (var file in files)
        {
            InstalledGame? game = null;
            try
            {
                if (new FileInfo(file).Length <= MaxManifestBytes)
                {
                    game = ParseEpicManifest(File.ReadAllBytes(file));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipped unreadable Epic manifest {File}.", Path.GetFileName(file));
            }

            if (game is not null)
            {
                yield return game;
            }
        }
    }

    /// <summary>One Epic <c>*.item</c> manifest (JSON): null unless it is a complete game install.</summary>
    internal static InstalledGame? ParseEpicManifest(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            GetString(root, "AppName") is not { } appName ||
            (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        // Engines, plugins and tools are listed too; keep entries the launcher tags as games (untagged ones are kept).
        if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array &&
            !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var name = GetString(root, "DisplayName") ?? appName;
        var executable = GetString(root, "LaunchExecutable") is { } launch ? Path.GetFileName(launch.Replace('/', '\\')) : null;
        var processName = executable is not null && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                          executable.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? executable
            : null;

        return new InstalledGame(GameLaunchTypes.Epic, appName, Trim(name), processName);
    }

    /// <summary>Marks the games a catalog entry already points at (same launch type and target).</summary>
    public static IReadOnlyList<DiscoveredGame> CompareWithCatalog(IEnumerable<InstalledGame> installed, IEnumerable<GameCatalogEntity> catalog)
    {
        var known = catalog
            .Select(entry => (entry.LaunchType.ToLowerInvariant(), entry.Target.ToUpperInvariant()))
            .ToHashSet();

        return installed
            .Select(game => new DiscoveredGame
            {
                LaunchType = game.LaunchType,
                Target = game.Target,
                Name = game.Name,
                ProcessName = game.ProcessName,
                InCatalog = known.Contains((game.LaunchType, game.Target.ToUpperInvariant()))
            })
            .ToList();
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string Trim(string name)
    {
        name = name.Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    [GeneratedRegex("\"(appid|name|StateFlags)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfFieldRegex();
}
