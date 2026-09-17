namespace BaronDeskAgent.ServiceCore;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BaronDesk Agent ServiceCore started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "BaronDesk Agent is running at {Time}",
                DateTimeOffset.Now);

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        _logger.LogInformation("BaronDesk Agent ServiceCore stopped.");
    }
}