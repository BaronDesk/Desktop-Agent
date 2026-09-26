using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Turns a catalog entry from the backend into a <see cref="GameCatalogEntity"/>, rejecting anything the agent could
/// not start safely. The agent builds every command line itself from these checked fields.
/// </summary>
internal static class GameCatalogValidator
{
    public const int MaxGameIdLength = 128;
    private const int MaxNameLength = 200;
    private const int MaxPathLength = 1024;
    private const int MaxArgumentsLength = 1024;
    private const int MaxSteamAppIdLength = 10;
    private const int MaxEpicAppNameLength = 128;
    private const int MaxProcessNameLength = 64;

    /// <summary>
    /// Processes that are never "the game": closing them at session end would break Windows, the agent or the
    /// launcher, and seeing them running says nothing about the game.
    /// </summary>
    private static readonly FrozenSet<string> ReservedProcessNames = new[]
    {
        "explorer", "dwm", "csrss", "winlogon", "wininit", "lsass", "services", "smss", "svchost", "sihost",
        "fontdrvhost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "conhost", "cmd", "powershell", "pwsh",
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper",
        "barondesk.lockui", "barondeskagent.servicecore"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryCreate(
        CatalogGame game,
        DateTimeOffset now,
        [NotNullWhen(true)] out GameCatalogEntity? entity,
        [NotNullWhen(false)] out string? error)
    {
        entity = null;

        if (!TryNormalizeGameId(game.GameId, out var gameId))
        {
            error = $"gameId is required (1-{MaxGameIdLength} characters, no control characters).";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(game.Name) ? gameId : game.Name.Trim();
        if (name.Length > MaxNameLength || HasControlCharacters(name))
        {
            error = $"name must be at most {MaxNameLength} characters, without control characters.";
            return false;
        }

        var launchType = string.IsNullOrWhiteSpace(game.LaunchType) ? GameLaunchTypes.Exe : game.LaunchType.Trim().ToLowerInvariant();
        if (!GameLaunchTypes.All.Contains(launchType))
        {
            error = $"launchType '{game.LaunchType}' is not supported (expected {string.Join(", ", GameLaunchTypes.All)}).";
            return false;
        }

        var target = game.Target?.Trim() ?? string.Empty;
        var targetError = launchType switch
        {
            GameLaunchTypes.Exe => ValidateExecutablePath(target),
            GameLaunchTypes.Steam => target.Length is > 0 and <= MaxSteamAppIdLength && target.All(char.IsAsciiDigit)
                ? null
                : "target must be a Steam app id (digits only).",
            _ => target.Length is > 0 and <= MaxEpicAppNameLength && target.All(IsEpicAppNameCharacter)
                ? null
                : "target must be an Epic AppName (letters, digits, '.', '_' or '-')."
        };
        if (targetError is not null)
        {
            error = targetError;
            return false;
        }

        // The Epic launcher URI carries no arguments; they are ignored rather than failing the entry.
        var arguments = launchType == GameLaunchTypes.Epic || string.IsNullOrWhiteSpace(game.Arguments) ? null : game.Arguments.Trim();
        if (arguments is not null && (arguments.Length > MaxArgumentsLength || HasControlCharacters(arguments)))
        {
            error = $"arguments must be at most {MaxArgumentsLength} characters, without control characters.";
            return false;
        }

        var workingDirectory = launchType != GameLaunchTypes.Exe || string.IsNullOrWhiteSpace(game.WorkingDirectory)
            ? null
            : game.WorkingDirectory.Trim();
        if (workingDirectory is not null && !IsSafeFullyQualifiedPath(workingDirectory))
        {
            error = "workingDirectory must be a fully qualified folder path.";
            return false;
        }

        string? processName = null;
        if (!string.IsNullOrWhiteSpace(game.ProcessName) && !TryNormalizeProcessName(game.ProcessName, out processName, out error))
        {
            return false;
        }

        entity = new GameCatalogEntity
        {
            GameId = gameId,
            Name = name,
            LaunchType = launchType,
            Target = target,
            LaunchArguments = arguments,
            WorkingDirectory = workingDirectory,
            ProcessName = processName,
            CreatedAt = now,
            UpdatedAt = now
        };
        error = null;
        return true;
    }

    /// <summary>A usable game id: trimmed, 1–128 characters, no control characters.</summary>
    public static bool TryNormalizeGameId(string? value, [NotNullWhen(true)] out string? gameId)
    {
        gameId = value?.Trim();
        if (string.IsNullOrEmpty(gameId) || gameId.Length > MaxGameIdLength || HasControlCharacters(gameId))
        {
            gameId = null;
            return false;
        }

        return true;
    }

    /// <summary><c>cs2.exe</c> or <c>cs2</c> → <c>cs2</c> (the form <see cref="System.Diagnostics.Process.GetProcessesByName(string)"/> expects).</summary>
    public static bool TryNormalizeProcessName(
        string value,
        [NotNullWhen(true)] out string? processName,
        [NotNullWhen(false)] out string? error)
    {
        processName = value.Trim();
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processName = processName[..^4];
        }

        if (processName.Length is 0 or > MaxProcessNameLength ||
            processName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            processName = null;
            error = $"processName must be an executable file name (at most {MaxProcessNameLength} characters), not a path.";
            return false;
        }

        if (ReservedProcessNames.Contains(processName))
        {
            error = $"processName '{processName}' is a system or launcher process, not the game.";
            processName = null;
            return false;
        }

        error = null;
        return true;
    }

    private static string? ValidateExecutablePath(string path) =>
        IsSafeFullyQualifiedPath(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? null
            : "target must be the fully qualified path of an .exe file.";

    private static bool IsSafeFullyQualifiedPath(string path) =>
        path.Length <= MaxPathLength &&
        Path.IsPathFullyQualified(path) &&
        !path.Contains('"') &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        !HasControlCharacters(path);

    private static bool IsEpicAppNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);
}
