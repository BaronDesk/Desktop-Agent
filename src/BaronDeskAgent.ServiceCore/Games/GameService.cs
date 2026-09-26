using System.Diagnostics;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Launches catalog games in the gamer's session and tracks the one the agent started, so it can be stopped
/// at session end.
/// </summary>
/// <remarks>
/// <c>exe</c> games are tracked by their process tree. Launcher games (Steam, Epic) are tracked by the catalog's
/// <c>processName</c> only: the launcher process the agent starts is not the game, and is never killed. A game
/// without a <c>processName</c> is only visible while its launched process runs.
/// </remarks>
public sealed class GameService : IDisposable
{
    /// <summary>How long a launched game counts as running before its process shows up (launcher start, updates).</summary>
    public static readonly TimeSpan LaunchGracePeriod = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);

    private readonly GameCatalogRepository _catalog;
    private readonly GameLaunchResolver _resolver;
    private readonly IGameLibraryLocator _libraries;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameService> _logger;
    private readonly object _gate = new();

    // The last launched game stays tracked until it is stopped or replaced, even while it is not seen running,
    // so a game that starts late (after a launcher update) is still closed at session end.
    private Process? _process;
    private string? _gameId;
    private string? _processName;
    private long _launchedAt;
    private bool _seenRunning;

    public GameService(
        GameCatalogRepository catalog,
        GameLaunchResolver resolver,
        IGameLibraryLocator libraries,
        InteractiveProcessLauncher launcher,
        TimeProvider timeProvider,
        ILogger<GameService> logger)
    {
        _catalog = catalog;
        _resolver = resolver;
        _libraries = libraries;
        _launcher = launcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Id of the game the agent launched, while it is running (or still starting).</summary>
    public string? CurrentGameId
    {
        get
        {
            lock (_gate)
            {
                if (_gameId is null)
                {
                    return null;
                }

                if (_processName is not null && GameProcesses.IsRunning(_processName))
                {
                    _seenRunning = true;
                    return _gameId;
                }

                if (_process is { HasExited: false })
                {
                    return _gameId;
                }

                // Not seen yet: the launcher or bootstrapper may still be starting it. Once seen and gone, it exited.
                return !_seenRunning && _timeProvider.GetElapsedTime(_launchedAt) < LaunchGracePeriod ? _gameId : null;
            }
        }
    }

    /// <exception cref="GameNotFoundException">The id is not in the local catalog.</exception>
    /// <exception cref="GameNotInstalledException">The entry cannot be launched on this station.</exception>
    /// <exception cref="FileNotFoundException">The executable disappeared between the check and the launch.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started in the user's session.</exception>
    public async Task LaunchAsync(string gameId, CancellationToken cancellationToken)
    {
        var entry = await _catalog.GetByIdAsync(gameId, cancellationToken)
            ?? throw new GameNotFoundException(gameId);

        if (string.Equals(CurrentGameId, gameId, StringComparison.Ordinal))
        {
            _logger.LogInformation("Game {GameId} is already running.", gameId);
            return;
        }

        // Resolved before stopping the current game, so a request for a missing game leaves the running one alone.
        var resolution = _resolver.Resolve(entry, _libraries.Discover());
        if (resolution.Command is not { } command)
        {
            throw new GameNotInstalledException(gameId, resolution.Reason!);
        }

        await StopCurrentGameAsync(cancellationToken);

        var process = _launcher.Launch(command.ExecutablePath, command.Arguments, command.WorkingDirectory);
        if (entry.LaunchType == GameLaunchTypes.Exe)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnProcessExited(process, gameId);
        }
        else
        {
            // steam.exe / EpicGamesLauncher.exe: forwards the request and exits, or *is* the launcher. Never tracked.
            process.Dispose();
            process = null;
        }

        lock (_gate)
        {
            _process = process;
            _gameId = gameId;
            _processName = entry.ProcessName;
            _launchedAt = _timeProvider.GetTimestamp();
            _seenRunning = false;
        }

        if (entry.LaunchType != GameLaunchTypes.Exe && entry.ProcessName is null)
        {
            _logger.LogWarning("Game {GameId} has no processName in the catalog: it cannot be tracked or closed at session end.", gameId);
        }

        _logger.LogInformation("Launched {GameName} ({GameId}) via {LaunchType}.", entry.Name, gameId, entry.LaunchType);
    }

    /// <summary>
    /// Closes the tracked game: its launched process tree, then every process with its <c>processName</c> in the
    /// gamer's session. Each gets a graceful close first, then a process-tree kill.
    /// </summary>
    public async Task StopCurrentGameAsync(CancellationToken cancellationToken)
    {
        Process? process;
        string? gameId;
        string? processName;
        lock (_gate)
        {
            process = _process;
            gameId = _gameId;
            processName = _processName;
            _process = null;
            _gameId = null;
            _processName = null;
        }

        if (process is not null)
        {
            using (process)
            {
                await StopProcessAsync(process, gameId, cancellationToken);
            }
        }

        if (processName is null)
        {
            return;
        }

        foreach (var match in GameProcesses.FindInGamerSession(processName))
        {
            using (match)
            {
                await StopProcessAsync(match, gameId, cancellationToken);
            }
        }
    }

    /// <summary>Releases the process handle. Stopping the service deliberately does not kill the gamer's game.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private async Task StopProcessAsync(Process process, string? gameId, CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            _logger.LogInformation("Stopping game {GameId} (Pid={Pid}).", gameId, process.Id);

            // CloseMainWindow only works when the service shares the game's session (development);
            // from Session 0 it returns false and the kill below does the work.
            if (process.CloseMainWindow() && await WaitForExitAsync(process, GracefulCloseTimeout, cancellationToken))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await WaitForExitAsync(process, KillTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Could not terminate game {GameId}.", gameId);
        }
    }

    private void OnProcessExited(Process process, string gameId)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            _process = null;

            // With a processName the game may live on in a child (bootstrapper): keep tracking it by name.
            if (_processName is not null)
            {
                process.Dispose();
                return;
            }

            _gameId = null;
        }

        _logger.LogInformation("Game {GameId} exited.", gameId);
        process.Dispose();
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

public sealed class GameNotFoundException(string gameId)
    : Exception($"Game '{gameId}' is not in this station's catalog.")
{
    public string GameId { get; } = gameId;
}

public sealed class GameNotInstalledException(string gameId, string reason)
    : Exception($"Game '{gameId}' cannot be launched on this station: {reason}")
{
    public string GameId { get; } = gameId;

    public string Reason { get; } = reason;
}
