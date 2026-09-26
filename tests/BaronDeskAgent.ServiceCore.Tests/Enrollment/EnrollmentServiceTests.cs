using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Credentials;
using BaronDeskAgent.ServiceCore.Enrollment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Enrollment;

public sealed class EnrollmentServiceTests
{
    private const string OneTimeToken = "enroll-7f3c9a1b2d4e";
    private const string StationJwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzdGF0aW9uLTAxIn0.c2lnbmF0dXJl";

    private readonly FakeTokenStore _credentials = new();
    private readonly FakeTokenStore _enrollmentTokens = new();
    private readonly FakeKeyStore _keys = new();
    private readonly FakeEnrollmentClient _client = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-25T10:00:00Z"));
    private readonly CapturingLogger _logger = new();
    private readonly AgentOptions _options = new() { SerialNumber = "STATION-07", EnrollmentPollSeconds = 15 };

    [Fact]
    public async Task An_enrolled_station_never_contacts_the_enrollment_endpoint()
    {
        _credentials.Token = StationJwt;
        _enrollmentTokens.Token = OneTimeToken;

        await CreateService().EnsureEnrolledAsync(CancellationToken.None);

        Assert.Empty(_client.Requests);
    }

    [Fact]
    public async Task Without_an_enrollment_token_nothing_is_sent()
    {
        await CreateService().EnsureEnrolledAsync(CancellationToken.None);

        Assert.Empty(_client.Requests);
        Assert.Null(_credentials.Token);
    }

    [Fact]
    public async Task Pending_until_approved_then_the_credential_is_stored_and_the_token_forgotten()
    {
        _enrollmentTokens.Token = OneTimeToken;
        _client.Enqueue(Pending(), Pending(), new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = StationJwt, MachineId = "m-1" });

        await RunAsync(CreateService());

        Assert.Equal(3, _client.Requests.Count);
        Assert.Equal(StationJwt, _credentials.Token);
        Assert.Null(_enrollmentTokens.Token);

        // Every poll presents the same station key: the backend binds the approval to it.
        Assert.Single(_client.Requests.Select(request => request.AgentPublicKey).Distinct());
    }

    [Fact]
    public async Task The_request_carries_the_token_and_a_public_key_it_proves_possession_of()
    {
        _enrollmentTokens.Token = OneTimeToken;
        _client.Enqueue(new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = StationJwt });

        await RunAsync(CreateService());

        var request = Assert.Single(_client.Requests);
        Assert.Equal(OneTimeToken, request.OneTimeToken);
        Assert.Equal("STATION-07", request.SerialNumber);
        Assert.False(string.IsNullOrEmpty(request.Ip));

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.AgentPublicKey), out _);
        Assert.Equal(_keys.PublicKey, request.AgentPublicKey);

        var signature = Convert.FromBase64String(request.Signature);
        Assert.True(publicKey.VerifyData(EnrollmentService.GetSigningInput(request), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        // Any edit to a signed field breaks the signature (e.g. a relayed request with a different token).
        var tampered = request with { OneTimeToken = "enroll-stolen" };
        Assert.False(publicKey.VerifyData(EnrollmentService.GetSigningInput(tampered), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public async Task A_rejection_spends_the_token_and_stores_no_credential()
    {
        _enrollmentTokens.Token = OneTimeToken;
        _client.Enqueue(new EnrollmentResponse { Status = EnrollmentStatuses.Rejected, Reason = "declined by admin" });

        await RunAsync(CreateService());

        Assert.Single(_client.Requests);
        Assert.Null(_credentials.Token);
        Assert.Null(_enrollmentTokens.Token);
    }

    [Fact]
    public async Task Transient_failures_and_unusable_answers_are_retried()
    {
        _enrollmentTokens.Token = OneTimeToken;
        _client.Enqueue(
            new HttpRequestException("connection refused"),
            new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = "has space" },
            new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = StationJwt });

        await RunAsync(CreateService());

        Assert.Equal(3, _client.Requests.Count);
        Assert.Equal(StationJwt, _credentials.Token);
    }

    [Fact]
    public async Task The_development_config_token_is_used_when_none_is_provisioned()
    {
        _options.EnrollmentToken = OneTimeToken;
        _client.Enqueue(new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = StationJwt });

        await RunAsync(CreateService());

        Assert.Equal(OneTimeToken, Assert.Single(_client.Requests).OneTimeToken);
        Assert.Equal(StationJwt, _credentials.Token);
        Assert.Equal(0, _enrollmentTokens.Deletes);
    }

    [Fact]
    public async Task Neither_token_is_ever_logged()
    {
        _enrollmentTokens.Token = OneTimeToken;
        _client.Enqueue(Pending(), new HttpRequestException("boom"), new EnrollmentResponse { Status = EnrollmentStatuses.Enrolled, StationToken = StationJwt });

        await RunAsync(CreateService());

        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, message => message.Contains(OneTimeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Messages, message => message.Contains(StationJwt, StringComparison.Ordinal));
    }

    private static EnrollmentResponse Pending() => new() { Status = EnrollmentStatuses.Pending };

    private EnrollmentService CreateService() =>
        new(_credentials, _enrollmentTokens, _keys, _client, Options.Create(_options), _time, _logger);

    /// <summary>Runs enrollment, advancing the fake clock through every poll/backoff delay.</summary>
    private async Task RunAsync(EnrollmentService service)
    {
        var task = service.EnsureEnrolledAsync(CancellationToken.None);
        var watchdog = Stopwatch.StartNew();

        while (!task.IsCompleted)
        {
            Assert.True(watchdog.Elapsed < TimeSpan.FromSeconds(10), "Enrollment did not finish.");
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(5);
        }

        await task;
    }

    private sealed class FakeTokenStore : IStationCredentialStore, IEnrollmentTokenStore
    {
        public string? Token { get; set; }

        public int Deletes { get; private set; }

        public string? TryGetToken() => Token;

        public void SaveToken(string token) => Token = token;

        public bool DeleteToken()
        {
            Deletes++;
            var existed = Token is not null;
            Token = null;
            return existed;
        }
    }

    private sealed class FakeKeyStore : IStationKeyStore
    {
        private readonly byte[] _pkcs8;

        public FakeKeyStore()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _pkcs8 = key.ExportPkcs8PrivateKey();
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }

        public string PublicKey { get; }

        public ECDsa GetOrCreate()
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(_pkcs8, out _);
            return key;
        }

        public bool Delete() => false;
    }

    private sealed class FakeEnrollmentClient : IEnrollmentClient
    {
        private readonly ConcurrentQueue<object> _answers = new();

        public List<EnrollmentRequest> Requests { get; } = [];

        public Uri Endpoint { get; } = new("http://127.0.0.1:8443/enrollment/request");

        public void Enqueue(params object[] answers)
        {
            foreach (var answer in answers)
            {
                _answers.Enqueue(answer);
            }
        }

        public Task<EnrollmentResponse> RequestAsync(EnrollmentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!_answers.TryDequeue(out var answer))
            {
                return Task.FromResult(Pending());
            }

            return answer is Exception exception
                ? Task.FromException<EnrollmentResponse>(exception)
                : Task.FromResult((EnrollmentResponse)answer);
        }
    }

    private sealed class CapturingLogger : ILogger<EnrollmentService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception) + exception);
    }
}
