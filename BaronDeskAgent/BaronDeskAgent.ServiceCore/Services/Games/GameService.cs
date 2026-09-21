using System.Diagnostics;
using BaronDeskAgent.ServiceCore.Data.Entities;
using BaronDeskAgent.ServiceCore.Data.Repositories;

namespace BaronDeskAgent.ServiceCore.Services.Games;

public sealed class GameService : IDisposable
{
    private readonly GameCatalogRepository _catalogRepository;
    private readonly InteractiveProcessLauncher _processLauncher;
    private readonly ILogger<GameService> _logger;

    private readonly object _processLock = new();
    private Process? _currentProcess;
    private string? _currentRunningGameId;
    private DateTimeOffset? _gameStartedAt;
    private bool _disposed;

    public event Action<string, int>? OnGameStarted;
    public event Action<string, int>? OnGameExited;

    public GameService(
        GameCatalogRepository catalogRepository,
        InteractiveProcessLauncher processLauncher,
        ILogger<GameService> logger)
    {
        _catalogRepository = catalogRepository;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    public bool IsGameRunning
    {
        get
        {
            lock (_processLock)
            {
                return _currentProcess is { HasExited: false };
            }
        }
    }

    public string? CurrentRunningGameId
    {
        get
        {
            lock (_processLock)
            {
                return _currentProcess is { HasExited: false } ? _currentRunningGameId : null;
            }
        }
    }

    public int? CurrentProcessId
    {
        get
        {
            lock (_processLock)
            {
                return _currentProcess is { HasExited: false } ? _currentProcess.Id : null;
            }
        }
    }

    public TimeSpan? RunningDuration
    {
        get
        {
            lock (_processLock)
            {
                return _gameStartedAt.HasValue && IsGameRunning
                    ? DateTimeOffset.UtcNow - _gameStartedAt.Value
                    : null;
            }
        }
    }

    public async Task LaunchGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _logger.LogInformation("Game launch requested. GameId={GameId}", gameId);

        // 1. Resolve catalog entry
        var entry = await _catalogRepository.GetByIdAsync(gameId, cancellationToken);
        if (entry is null)
        {
            throw new KeyNotFoundException($"Game '{gameId}' was not found in the local catalog.");
        }

        if (!File.Exists(entry.ExecutablePath))
        {
            throw new FileNotFoundException(
                $"Executable not found at '{entry.ExecutablePath}' for game '{gameId}'.",
                entry.ExecutablePath);
        }

        // 2. Handle active game conflict
        bool isSameRunning = false;
        lock (_processLock)
        {
            if (_currentProcess is { HasExited: false })
            {
                if (string.Equals(_currentRunningGameId, gameId, StringComparison.OrdinalIgnoreCase))
                {
                    isSameRunning = true;
                }
            }
        }

        if (isSameRunning)
        {
            _logger.LogInformation("Game '{GameId}' is already active and running.", gameId);
            return;
        }

        // Terminate any existing running game before starting the new one
        await StopCurrentGameAsync(cancellationToken);

        // 3. Launch the game process
        Process process;
        try
        {
            process = _processLauncher.Launch(
                entry.ExecutablePath,
                entry.LaunchArguments,
                entry.WorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch process for game {GameId} at {Path}", gameId, entry.ExecutablePath);
            throw;
        }

        // 4. Track process lifecycle
        lock (_processLock)
        {
            _currentProcess = process;
            _currentRunningGameId = gameId;
            _gameStartedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => HandleProcessExited(process, gameId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable process exit events for Pid={Pid}.", process.Id);
        }

        _logger.LogInformation(
            "Game '{GameName}' ({GameId}) successfully launched. Pid={Pid}",
            entry.Name, gameId, process.Id);

        OnGameStarted?.Invoke(gameId, process.Id);
    }

    public async Task StopGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        bool isTargetRunning = false;
        lock (_processLock)
        {
            if (_currentProcess is { HasExited: false } &&
                string.Equals(_currentRunningGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                isTargetRunning = true;
            }
        }

        if (isTargetRunning)
        {
            await StopCurrentGameAsync(cancellationToken);
        }
        else
        {
            _logger.LogInformation("Game '{GameId}' is not currently running.", gameId);
        }
    }

    public async Task StopCurrentGameAsync(
        CancellationToken cancellationToken = default)
    {
        Process? processToStop;
        string? gameId;

        lock (_processLock)
        {
            processToStop = _currentProcess;
            gameId = _currentRunningGameId;

            _currentProcess = null;
            _currentRunningGameId = null;
            _gameStartedAt = null;
        }

        if (processToStop is null)
        {
            return;
        }

        try
        {
            if (processToStop.HasExited)
            {
                processToStop.Dispose();
                return;
            }

            _logger.LogInformation(
                "Stopping game '{GameId}' (Pid={Pid}). Attempting graceful close...",
                gameId, processToStop.Id);

            // Try graceful close first
            bool closedGracefully = false;
            try
            {
                if (processToStop.CloseMainWindow())
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    await processToStop.WaitForExitAsync(linkedCts.Token);
                    closedGracefully = true;
                    _logger.LogInformation("Game '{GameId}' closed gracefully.", gameId);
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation reached, will proceed to force kill
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Notice during graceful close attempt for Pid={Pid}.", processToStop.Id);
            }

            // If not exited gracefully, force kill entire process tree
            if (!closedGracefully && !processToStop.HasExited)
            {
                _logger.LogWarning("Game '{GameId}' (Pid={Pid}) did not exit gracefully; terminating entire process tree.", gameId, processToStop.Id);
                try
                {
                    processToStop.Kill(entireProcessTree: true);
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await processToStop.WaitForExitAsync(killTimeout.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while killing process tree for Pid={Pid}.", processToStop.Id);
                }
            }
        }
        finally
        {
            try
            {
                processToStop.Dispose();
            }
            catch
            {
                // Ignore disposal errors on dead process
            }
        }
    }

    private void HandleProcessExited(Process process, string gameId)
    {
        int exitCode = -1;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            // ExitCode might not be readable if permissions change
        }

        lock (_processLock)
        {
            if (ReferenceEquals(_currentProcess, process))
            {
                _currentProcess = null;
                _currentRunningGameId = null;
                _gameStartedAt = null;
            }
        }

        _logger.LogInformation("Tracked game '{GameId}' has exited. ExitCode={ExitCode}", gameId, exitCode);
        OnGameExited?.Invoke(gameId, exitCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopCurrentGameAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notice during GameService disposal.");
        }
    }
}