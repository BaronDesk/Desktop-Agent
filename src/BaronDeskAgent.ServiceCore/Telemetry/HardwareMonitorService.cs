using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Samples sensors on an adaptive cadence (policy cadence during a session, slower while idle), streams only
/// meaningful changes, and raises hardware alerts. A sensor failure never stops the agent.
/// </summary>
public sealed class HardwareMonitorService : BackgroundService
{
    private const double IdleCadenceMultiplier = 3.0;

    private readonly HardwareSensorReader _sensorReader;
    private readonly HardwareTelemetryMapper _mapper;
    private readonly TelemetryDeltaFilter _deltaFilter;
    private readonly HardwareAlertEvaluator _alertEvaluator;
    private readonly TelemetryPublisher _publisher;
    private readonly StationController _station;
    private readonly ServerClock _serverClock;
    private readonly IPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HardwareMonitorService> _logger;

    public HardwareMonitorService(
        HardwareSensorReader sensorReader,
        HardwareTelemetryMapper mapper,
        TelemetryDeltaFilter deltaFilter,
        HardwareAlertEvaluator alertEvaluator,
        TelemetryPublisher publisher,
        StationController station,
        ServerClock serverClock,
        IPolicyStore policyStore,
        TimeProvider timeProvider,
        ILogger<HardwareMonitorService> logger)
    {
        _sensorReader = sensorReader;
        _mapper = mapper;
        _deltaFilter = deltaFilter;
        _alertEvaluator = alertEvaluator;
        _publisher = publisher;
        _station = station;
        _serverClock = serverClock;
        _policyStore = policyStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _sensorReader.Open();
        }
        catch (Exception ex)
        {
            // No admin rights, or the sensor driver is blocked by AV/HVCI. Everything else keeps working.
            _logger.LogError(ex, "Hardware sensors are unavailable; telemetry and hardware alerts are disabled.");
            return;
        }

        using var timer = new PeriodicTimer(CurrentInterval(), _timeProvider);

        void OnPolicyUpdated(StationPolicy _) => timer.Period = CurrentInterval();
        _policyStore.PolicyUpdated += OnPolicyUpdated;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SampleAsync(timer.Period, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Hardware sampling failed; retrying on the next tick.");
                }

                var interval = CurrentInterval();
                if (timer.Period != interval)
                {
                    timer.Period = interval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _policyStore.PolicyUpdated -= OnPolicyUpdated;
            _sensorReader.Dispose();
        }
    }

    private async Task SampleAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var telemetry = _sensorReader.Read(_serverClock.UtcNow);

        if (_publisher.IsOnline)
        {
            var changed = _deltaFilter.Filter(_mapper.Map(telemetry), interval);
            if (changed.Count > 0)
            {
                await _publisher.PublishTelemetryAsync(changed, cancellationToken);
            }
        }
        else
        {
            // Live telemetry is not buffered; start with a full snapshot after reconnecting.
            _deltaFilter.Reset();
        }

        foreach (var alert in _alertEvaluator.Evaluate(telemetry))
        {
            _logger.LogWarning("Hardware alert: {Severity} {Type} - {Detail}", alert.Severity, alert.Type, alert.Detail);
            await _publisher.PublishAlertAsync(alert, cancellationToken);
        }
    }

    private TimeSpan CurrentInterval()
    {
        var cadence = _policyStore.CurrentPolicy.TelemetryCadenceSeconds;
        var multiplier = _station.GetSnapshot().SessionId is null ? IdleCadenceMultiplier : 1.0;
        return TimeSpan.FromSeconds(cadence * multiplier);
    }
}
