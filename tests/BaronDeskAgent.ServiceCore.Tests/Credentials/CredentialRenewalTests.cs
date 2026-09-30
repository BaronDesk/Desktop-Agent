using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Logging;

namespace BaronDeskAgent.ServiceCore.Tests.Credentials;

public sealed class CredentialRenewalTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzdGF0aW9uLTAxIn0.cmVuZXdlZA";

    private readonly FakeStore _store = new();
    private readonly CapturingLogger _logger = new();

    [Fact]
    public void Stores_the_renewed_token_for_the_next_connect()
    {
        Assert.True(CredentialRenewal.TryStore(_store, new StationCredentialPayload { StationToken = Token }, _logger));
        Assert.Equal(Token, _store.TryGetToken());
    }

    [Fact]
    public void Ignores_an_empty_token_and_keeps_the_current_one()
    {
        _store.SaveToken("current");
        Assert.False(CredentialRenewal.TryStore(_store, new StationCredentialPayload { StationToken = "  " }, _logger));
        Assert.Equal("current", _store.TryGetToken());
    }

    [Fact]
    public void A_refused_token_is_never_logged()
    {
        _store.Refuse = true;
        Assert.False(CredentialRenewal.TryStore(_store, new StationCredentialPayload { StationToken = Token }, _logger));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void The_stored_token_is_never_logged()
    {
        CredentialRenewal.TryStore(_store, new StationCredentialPayload { StationToken = Token }, _logger);
        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains(Token, StringComparison.Ordinal));
    }

    private sealed class FakeStore : IStationCredentialStore
    {
        private string? _token;

        public bool Refuse { get; set; }

        public string? TryGetToken() => _token;

        public void SaveToken(string token)
        {
            if (Refuse)
            {
                throw new ArgumentException($"bad token {token}");
            }

            _token = token;
        }

        public bool DeleteToken()
        {
            var had = _token is not null;
            _token = null;
            return had;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
}
