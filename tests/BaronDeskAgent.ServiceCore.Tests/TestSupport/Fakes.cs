using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Persistence;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Tests.TestSupport;

internal sealed class FakePolicyStore(StationPolicy? policy = null) : IPolicyStore
{
    public StationPolicy CurrentPolicy { get; set; } = policy ?? new StationPolicy();

    public event Action<StationPolicy>? PolicyUpdated;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<StationPolicy> UpdatePolicyAsync(PolicyUpdatePayload update, CancellationToken cancellationToken = default)
    {
        CurrentPolicy = CurrentPolicy.Apply(update, DateTimeOffset.UnixEpoch);
        PolicyUpdated?.Invoke(CurrentPolicy);
        return Task.FromResult(CurrentPolicy);
    }
}

internal sealed class FakeLockScreen : ILockScreen
{
    public bool Confirms { get; set; } = true;

    public bool IsShown { get; private set; } = true;

    public Task<bool> ShowAsync(CancellationToken cancellationToken)
    {
        IsShown = true;
        return Task.FromResult(Confirms);
    }

    public Task<bool> HideAsync(CancellationToken cancellationToken)
    {
        IsShown = false;
        return Task.FromResult(Confirms);
    }
}

internal sealed record SentFrame(string Type, JsonElement Payload, Guid Id);

internal sealed class FakeServerConnection : IServerConnection
{
    public ConcurrentQueue<SentFrame> Sent { get; } = new();

    public bool IsReady { get; set; } = true;

    public event Action<bool>? ReadyChanged;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void MarkReady() => ReadyChanged?.Invoke(true);

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Guid> SendAsync<T>(string type, T payload, JsonTypeInfo<T> payloadTypeInfo, CancellationToken cancellationToken, Guid? messageId = null)
    {
        var id = messageId ?? Guid.NewGuid();
        Sent.Enqueue(new SentFrame(type, JsonSerializer.SerializeToElement(payload, payloadTypeInfo), id));
        return Task.FromResult(id);
    }

    public Task<ReceiveResult> ReceiveAsync(CancellationToken cancellationToken) => Task.FromResult(ReceiveResult.Closed);

    public IReadOnlyList<SentFrame> OfType(string type) => Sent.Where(frame => frame.Type == type).ToList();
}

/// <summary>A migrated SQLite database in a temp file, deleted on dispose.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"barondesk-test-{Guid.NewGuid():N}.sqlite");

    private TestDatabase()
    {
        Database = new AgentDatabase(_path);
    }

    public AgentDatabase Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await new DatabaseInitializer(database.Database, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        return database;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal static class Envelopes
{
    public static Envelope<JsonElement> Create(string type, object? payload, long seq, DateTimeOffset ts, Guid? id = null) => new()
    {
        Type = type,
        Id = id ?? Guid.NewGuid(),
        Ts = ts,
        Seq = seq,
        Payload = payload is null ? default : JsonSerializer.SerializeToElement(payload)
    };
}
