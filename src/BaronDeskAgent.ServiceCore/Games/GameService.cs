using System.Diagnostics;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Launches catalog games in the gamer's session and tracks the one the agent started, so it can be stopped
/// at session end.
/// </summary>
/// <remarks>
/// Only the launched process tree is tracked. Games started through a launcher that exits (Steam, Epic)
/// detach from that tree and are not stopped; closing them needs per-game knowledge (catalog work).
/// </remarks>
public sealed class GameService : IDisposable
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);

    private readonly GameCatalogRepository _catalog;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly ILogger<GameService> _logger;
    private readonly object _gate = new();

    private Process? _process;
    private string? _gameId;

    public GameService(GameCatalogRepository catalog, InteractiveProcessLauncher launcher, ILogger<GameService> logger)
    {
        _catalog = catalog;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Id of the game the agent launched, while it is still running.</summary>
    public string? CurrentGameId
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } ? _gameId : null;
            }
        }
    }

    /// <exception cref="GameNotFoundException">The id is not in the local catalog.</exception>
    /// <exception cref="FileNotFoundException">The catalog entry points to a missing executable.</exception>
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

        await StopCurrentGameAsync(cancellationToken);

        var process = _launcher.Launch(entry.ExecutablePath, entry.LaunchArguments, entry.WorkingDirectory);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(process, gameId);

        lock (_gate)
        {
            _process = process;
            _gameId = gameId;
        }

        _logger.LogInformation("Launched {GameName} ({GameId}), Pid={Pid}.", entry.Name, gameId, process.Id);
    }

    /// <summary>Closes the tracked game: a graceful close first, then the whole process tree.</summary>
    public async Task StopCurrentGameAsync(CancellationToken cancellationToken)
    {
        Process? process;
        string? gameId;
        lock (_gate)
        {
            process = _process;
            gameId = _gameId;
            _process = null;
            _gameId = null;
        }

        if (process is null)
        {
            return;
        }

        using (process)
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

            try
            {
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, KillTimeout, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogError(ex, "Could not terminate game {GameId} (Pid={Pid}).", gameId, process.Id);
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

    private void OnProcessExited(Process process, string gameId)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            _process = null;
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
