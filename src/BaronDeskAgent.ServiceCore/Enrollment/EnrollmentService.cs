using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Enrollment;

/// <summary>
/// The station's first contact with the backend (Auth &amp; Security AS1): trades the one-time enrollment token for
/// the long-lived station credential.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The station generates its own key pair (<see cref="IStationKeyStore"/>); the private key never leaves it.</item>
/// <item>It sends the one-time token, MAC/IP and its public key, signed with the private key (proof of possession).</item>
/// <item>While an admin has not approved it (<c>PENDING</c>) it asks again every <see cref="AgentOptions.EnrollmentPollSeconds"/>.</item>
/// <item><c>ENROLLED</c>: the station JWT goes to DPAPI and the one-time token is deleted.
/// <c>REJECTED</c>: the token is deleted and the station stays unenrolled (and locked).</item>
/// </list>
/// Neither token is ever logged. The station stays locked the whole time (fail closed).
/// </remarks>
public sealed class EnrollmentService
{
    internal const string SignatureDomain = "BARONDESK-ENROLL-V1";

    private readonly IStationCredentialStore _credentials;
    private readonly IEnrollmentTokenStore _enrollmentTokens;
    private readonly IStationKeyStore _keys;
    private readonly IEnrollmentClient _client;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        IStationCredentialStore credentials,
        IEnrollmentTokenStore enrollmentTokens,
        IStationKeyStore keys,
        IEnrollmentClient client,
        IOptions<AgentOptions> options,
        TimeProvider timeProvider,
        ILogger<EnrollmentService> logger)
    {
        _credentials = credentials;
        _enrollmentTokens = enrollmentTokens;
        _keys = keys;
        _client = client;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Returns immediately when a station credential exists. Otherwise runs enrollment until the station is enrolled,
    /// rejected, or no enrollment token is provisioned.
    /// </summary>
    public async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        if (_credentials.TryGetToken() is not null || !string.IsNullOrWhiteSpace(_options.StationToken))
        {
            return;
        }

        var storedToken = _enrollmentTokens.TryGetToken();
        var oneTimeToken = storedToken ?? (string.IsNullOrWhiteSpace(_options.EnrollmentToken) ? null : _options.EnrollmentToken.Trim());
        if (oneTimeToken is null)
        {
            _logger.LogWarning(
                "This station is not enrolled and no enrollment token is provisioned. Generate one in the dashboard and run " +
                "'BaronDeskAgent.ServiceCore.exe --set-enrollment-token' from an elevated prompt.");
            return;
        }

        _logger.LogInformation("Station is not enrolled: requesting enrollment at {Endpoint}.", _client.Endpoint);

        var failures = 0;
        var pendingLogged = false;

        while (true)
        {
            TimeSpan delay;
            try
            {
                var request = await BuildRequestAsync(oneTimeToken, cancellationToken);
                var response = await _client.RequestAsync(request, cancellationToken);
                failures = 0;
                delay = TimeSpan.FromSeconds(_options.EnrollmentPollSeconds);

                switch (response.Status.ToUpperInvariant())
                {
                    case EnrollmentStatuses.Enrolled when DpapiTokenStore.IsValidToken(response.StationToken):
                        _credentials.SaveToken(response.StationToken!);
                        ForgetEnrollmentToken(storedToken);
                        _logger.LogInformation("Station enrolled (machine {MachineId}); station credential stored.", response.MachineId ?? "unknown");
                        return;

                    case EnrollmentStatuses.Enrolled:
                        _logger.LogError("The server answered ENROLLED without a usable station token; asking again later.");
                        break;

                    case EnrollmentStatuses.Rejected:
                        ForgetEnrollmentToken(storedToken);
                        _logger.LogCritical(
                            "Enrollment rejected by the server ({Reason}). The station stays locked until a new enrollment token is provisioned.",
                            response.Reason ?? "no reason given");
                        return;

                    case EnrollmentStatuses.Pending:
                        if (!pendingLogged)
                        {
                            _logger.LogInformation(
                                "Enrollment pending: waiting for an admin to approve station {SerialNumber} in the dashboard.",
                                request.SerialNumber);
                            pendingLogged = true;
                        }

                        break;

                    default:
                        _logger.LogWarning("Unexpected enrollment status '{Status}'; asking again later.", response.Status);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                delay = ReconnectBackoff.Compute(
                    failures,
                    TimeSpan.FromSeconds(_options.ReconnectBaseDelaySeconds),
                    TimeSpan.FromSeconds(_options.ReconnectMaxDelaySeconds),
                    Random.Shared);

                _logger.LogWarning("Enrollment request failed ({Error}: {Message}); retrying in {Delay:F1}s.", ex.GetType().Name, ex.Message, delay.TotalSeconds);
            }

            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    /// <summary>The exact bytes covered by <see cref="EnrollmentRequest.Signature"/>; the backend rebuilds them to verify.</summary>
    internal static byte[] GetSigningInput(EnrollmentRequest request) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\n',
            SignatureDomain,
            request.OneTimeToken,
            request.SerialNumber,
            request.Mac,
            request.Ip,
            request.AgentPublicKey,
            request.SignedAt));

    private async Task<EnrollmentRequest> BuildRequestAsync(string oneTimeToken, CancellationToken cancellationToken)
    {
        var (mac, ip) = await LocalNetworkIdentity.ResolveAsync(_client.Endpoint, cancellationToken);

        using var key = _keys.GetOrCreate();
        var request = new EnrollmentRequest
        {
            OneTimeToken = oneTimeToken,
            Mac = mac,
            Ip = ip,
            SerialNumber = _options.ResolveSerialNumber(),
            MachineName = Environment.MachineName,
            AgentVersion = ConnectionWorker.AgentVersion,
            AgentPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            SignedAt = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            Signature = string.Empty
        };

        var signature = key.SignData(GetSigningInput(request), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return request with { Signature = Convert.ToBase64String(signature) };
    }

    /// <summary>The token is single-use: once the server has answered definitively it is useless, so drop it.</summary>
    private void ForgetEnrollmentToken(string? storedToken)
    {
        if (storedToken is not null)
        {
            _enrollmentTokens.DeleteToken();
        }
    }
}
