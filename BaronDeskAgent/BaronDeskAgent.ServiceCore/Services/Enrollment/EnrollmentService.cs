using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Security;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Services.Enrollment;

/// <summary>
/// Manages the station enrollment lifecycle.
/// 
/// States:
///   NOT_ENROLLED → Agent has no stored credential and needs to present a bootstrap token.
///   PENDING      → Enrollment request sent, awaiting admin approval on the server.
///   ENROLLED     → Station JWT received and persisted in DPAPI credential store.
///   REJECTED     → Server rejected the bootstrap token. Enrollment halted.
/// </summary>
public sealed class EnrollmentService
{
    private readonly IStationCredentialStore _credentialStore;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<EnrollmentService> _logger;
    private readonly object _stateLock = new();

    private EnrollmentState _state = EnrollmentState.NotEnrolled;
    private string? _stationJwt;
    private Guid? _stationId;

    public EnrollmentService(
        IStationCredentialStore credentialStore,
        IOptions<AgentOptions> options,
        ILogger<EnrollmentService> logger)
    {
        _credentialStore = credentialStore;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Current enrollment state.
    /// </summary>
    public EnrollmentState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Whether the station is fully enrolled and has a valid JWT.
    /// </summary>
    public bool IsEnrolled => State == EnrollmentState.Enrolled;

    /// <summary>
    /// The station JWT for WebSocket upgrade authentication.
    /// Null if not enrolled.
    /// </summary>
    public string? StationJwt
    {
        get
        {
            lock (_stateLock)
            {
                return _stationJwt;
            }
        }
    }

    /// <summary>
    /// Server-assigned station identifier.
    /// </summary>
    public Guid? StationId
    {
        get
        {
            lock (_stateLock)
            {
                return _stationId;
            }
        }
    }

    /// <summary>
    /// Initializes enrollment state by checking the DPAPI credential store.
    /// Called once at startup before the connection worker begins.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_credentialStore.HasToken)
        {
            var jwt = await _credentialStore.LoadTokenAsync();
            if (!string.IsNullOrWhiteSpace(jwt))
            {
                lock (_stateLock)
                {
                    _stationJwt = jwt;
                    _state = EnrollmentState.Enrolled;
                }

                _logger.LogInformation("Station enrollment state: ENROLLED (JWT loaded from DPAPI store).");
                return;
            }
        }

        lock (_stateLock)
        {
            _state = EnrollmentState.NotEnrolled;
        }

        _logger.LogInformation("Station enrollment state: NOT_ENROLLED. Enrollment required.");
    }

    /// <summary>
    /// Whether the agent should attempt enrollment on the current connection.
    /// True when NOT_ENROLLED and a bootstrap token is configured.
    /// </summary>
    public bool ShouldAttemptEnrollment
    {
        get
        {
            lock (_stateLock)
            {
                return _state == EnrollmentState.NotEnrolled &&
                       !string.IsNullOrWhiteSpace(_options.Value.BootstrapToken);
            }
        }
    }

    /// <summary>
    /// Builds the enrollment request envelope using configured bootstrap token and machine metadata.
    /// </summary>
    public EnrollmentRequest BuildEnrollmentRequest()
    {
        var serial = !string.IsNullOrWhiteSpace(_options.Value.SerialNumber)
            ? _options.Value.SerialNumber
            : Environment.MachineName;

        return new EnrollmentRequest
        {
            BootstrapToken = _options.Value.BootstrapToken ?? string.Empty,
            MachineName = Environment.MachineName,
            SerialNumber = serial,
            OsVersion = Environment.OSVersion.ToString(),
            AgentVersion = "1.0.0"
        };
    }

    /// <summary>
    /// Processes the server's enrollment response.
    /// On ENROLLED: persists the JWT and transitions state.
    /// On PENDING: stays in NotEnrolled for retry on next connection.
    /// On REJECTED: halts enrollment permanently.
    /// </summary>
    public async Task HandleEnrollmentResponseAsync(EnrollmentResponse response)
    {
        var status = response.Status?.ToUpperInvariant() ?? string.Empty;

        switch (status)
        {
            case "ENROLLED":
                if (string.IsNullOrWhiteSpace(response.StationJwt))
                {
                    _logger.LogError("Server returned ENROLLED status but no JWT. Staying unenrolled.");
                    return;
                }

                await _credentialStore.SaveTokenAsync(response.StationJwt);

                lock (_stateLock)
                {
                    _stationJwt = response.StationJwt;
                    _stationId = response.StationId;
                    _state = EnrollmentState.Enrolled;
                }

                _logger.LogInformation(
                    "Station ENROLLED successfully. StationId={StationId}, JWT persisted to DPAPI store.",
                    response.StationId);
                break;

            case "PENDING":
                lock (_stateLock)
                {
                    _state = EnrollmentState.Pending;
                }

                _logger.LogInformation(
                    "Enrollment PENDING admin approval. Message: {Message}",
                    response.Message ?? "(none)");
                break;

            case "REJECTED":
                lock (_stateLock)
                {
                    _state = EnrollmentState.Rejected;
                }

                _logger.LogCritical(
                    "Enrollment REJECTED by server. Reason: {Message}. Manual intervention required.",
                    response.Message ?? "(unknown)");
                break;

            default:
                _logger.LogWarning("Unknown enrollment status received: {Status}", response.Status);
                break;
        }
    }

    /// <summary>
    /// Clears stored credentials and resets enrollment state to NOT_ENROLLED.
    /// Used for administrative unenrollment.
    /// </summary>
    public async Task UnenrollAsync()
    {
        await _credentialStore.ClearTokenAsync();

        lock (_stateLock)
        {
            _stationJwt = null;
            _stationId = null;
            _state = EnrollmentState.NotEnrolled;
        }

        _logger.LogInformation("Station unenrolled. Credential cleared from DPAPI store.");
    }
}

/// <summary>
/// Station enrollment lifecycle states.
/// </summary>
public enum EnrollmentState
{
    /// <summary>
    /// No credential stored. Enrollment required.
    /// </summary>
    NotEnrolled,

    /// <summary>
    /// Enrollment request sent, awaiting admin approval.
    /// </summary>
    Pending,

    /// <summary>
    /// Station is enrolled. JWT is available for authentication.
    /// </summary>
    Enrolled,

    /// <summary>
    /// Server rejected the enrollment. Manual intervention required.
    /// </summary>
    Rejected
}
