using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Logging;

namespace BaronDeskAgent.ServiceCore.Tests.Credentials;

public sealed class DpapiStationCredentialStoreTests : IDisposable
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzdGF0aW9uLTAxIn0.c2lnbmF0dXJl";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"barondesk-cred-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly CapturingLogger _logger = new();
    private readonly DpapiStationCredentialStore _store;

    public DpapiStationCredentialStoreTests()
    {
        _path = Path.Combine(_directory, "station.credential");
        _store = new DpapiStationCredentialStore(_path, _logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void No_file_means_no_credential()
    {
        Assert.Null(_store.TryGetToken());
    }

    [Fact]
    public void Round_trips_and_rotates()
    {
        _store.SaveToken(Token);
        Assert.Equal(Token, _store.TryGetToken());

        _store.SaveToken("rotated-token");
        Assert.Equal("rotated-token", _store.TryGetToken());
    }

    [Fact]
    public void Delete_removes_the_credential()
    {
        _store.SaveToken(Token);

        Assert.True(_store.DeleteToken());
        Assert.Null(_store.TryGetToken());
        Assert.False(_store.DeleteToken());
    }

    [Fact]
    public void The_token_is_never_on_disk_in_plain_text()
    {
        _store.SaveToken(Token);

        var content = File.ReadAllBytes(_path);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(content), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, Encoding.Unicode.GetString(content), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Corrupt_or_foreign_blobs_fail_closed()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllBytes(_path, [1, 2, 3, 4]);
        Assert.Null(_store.TryGetToken());

        // Valid machine-scope DPAPI data from another purpose (different entropy) is not accepted either.
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.LocalMachine));
        Assert.Null(_store.TryGetToken());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("abc\r\nX-Injected: 1")]
    [InlineData("tokén")]
    public void Rejects_tokens_that_are_not_valid_header_values(string token)
    {
        Assert.Throws<ArgumentException>(() => _store.SaveToken(token));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Rejects_oversized_tokens()
    {
        Assert.Throws<ArgumentException>(() => _store.SaveToken(new string('a', DpapiStationCredentialStore.MaxTokenLength + 1)));
    }

    [Fact]
    public void The_file_is_not_readable_by_users_or_everyone()
    {
        _store.SaveToken(Token);

        var security = new FileInfo(_path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var forbidden = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
        };

        var grantees = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference);

        Assert.DoesNotContain(grantees, forbidden.Contains);
    }

    [Fact]
    public void The_token_never_appears_in_logs()
    {
        _store.SaveToken(Token);
        _store.TryGetToken();
        File.WriteAllBytes(_path, [9, 9, 9]);
        _store.TryGetToken();

        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, message => message.Contains(Token, StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<DpapiStationCredentialStore>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
}
