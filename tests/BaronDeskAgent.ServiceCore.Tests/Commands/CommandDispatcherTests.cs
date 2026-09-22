using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Commands;

public sealed class CommandDispatcherTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeServerConnection _connection = new();
    private readonly Dictionary<string, ScriptedHandler> _handlers = CommandTypes.All.ToDictionary(type => type, type => new ScriptedHandler(type));
    private TestDatabase _database = null!;
    private CommandDispatcher _dispatcher = null!;
    private long _seq;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();

        _handlers[CommandTypes.Lock].IsRestrictive = true;
        _handlers[CommandTypes.EndSession].IsRestrictive = true;

        var replayGuard = new ReplayGuard(new ServerClock(_time), Options.Create(new AgentOptions()));
        var outcomes = new CommandOutcomeStore(_database.Database, _time, NullLogger<CommandOutcomeStore>.Instance);

        _dispatcher = new CommandDispatcher(
            _handlers.Values,
            _connection,
            replayGuard,
            outcomes,
            NullLogger<CommandDispatcher>.Instance);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Unknown_types_are_rejected()
    {
        await DispatchAsync("FORMAT_DISK");

        Assert.Equal(NackCodes.UnknownType, SingleNack().GetProperty("code").GetString());
    }

    [Fact]
    public async Task Command_types_are_matched_case_sensitively()
    {
        await DispatchAsync("lock");

        Assert.Equal(NackCodes.UnknownType, SingleNack().GetProperty("code").GetString());
        Assert.Equal(0, _handlers[CommandTypes.Lock].Executions);
    }

    [Fact]
    public async Task A_successful_command_is_acked_once_and_never_executed_twice()
    {
        var id = Guid.NewGuid();

        await DispatchAsync(CommandTypes.LaunchGame, id: id);
        await DispatchAsync(CommandTypes.LaunchGame, id: id);

        Assert.Equal(1, _handlers[CommandTypes.LaunchGame].Executions);
        Assert.Equal(2, _connection.OfType(MessageTypes.CommandAck).Count);
    }

    [Fact]
    public async Task A_failed_command_is_executed_again_when_redelivered_and_never_falsely_acked()
    {
        var id = Guid.NewGuid();
        var handler = _handlers[CommandTypes.LaunchGame];

        handler.Result = CommandResult.Failed("not installed");
        await DispatchAsync(CommandTypes.LaunchGame, id: id);

        handler.Result = CommandResult.Success();
        await DispatchAsync(CommandTypes.LaunchGame, id: id);

        Assert.Equal(2, handler.Executions);
        Assert.Single(_connection.OfType(MessageTypes.CommandNack));
        Assert.Single(_connection.OfType(MessageTypes.CommandAck));
    }

    [Fact]
    public async Task Stale_commands_are_rejected()
    {
        await DispatchAsync(CommandTypes.Unlock, ts: _time.GetUtcNow().AddMinutes(-10));

        Assert.Equal(NackCodes.Stale, SingleNack().GetProperty("code").GetString());
        Assert.Equal(0, _handlers[CommandTypes.Unlock].Executions);
    }

    [Theory]
    [InlineData(CommandTypes.Lock)]
    [InlineData(CommandTypes.EndSession)]
    public async Task Restrictive_commands_are_executed_even_with_a_drifted_timestamp(string type)
    {
        await DispatchAsync(type, ts: _time.GetUtcNow().AddMinutes(-10));

        Assert.Equal(1, _handlers[type].Executions);
        Assert.Single(_connection.OfType(MessageTypes.CommandAck));
    }

    [Fact]
    public async Task Replayed_sequence_numbers_are_rejected_even_for_restrictive_commands()
    {
        await DispatchAsync(CommandTypes.Lock, seq: 7);
        await DispatchAsync(CommandTypes.Lock, seq: 7);

        Assert.Equal(1, _handlers[CommandTypes.Lock].Executions);
        Assert.Equal(NackCodes.Stale, SingleNack().GetProperty("code").GetString());
    }

    [Fact]
    public async Task Early_acknowledgement_is_the_only_reply()
    {
        var handler = _handlers[CommandTypes.Shutdown];
        handler.AcknowledgeEarly = true;
        handler.Result = CommandResult.Failed("shutdown.exe refused");

        await DispatchAsync(CommandTypes.Shutdown);

        Assert.Single(_connection.OfType(MessageTypes.CommandAck));
        Assert.Empty(_connection.OfType(MessageTypes.CommandNack));
    }

    [Fact]
    public async Task A_throwing_handler_is_nacked_as_exec_failed()
    {
        _handlers[CommandTypes.PolicyUpdate].Throw = true;

        await DispatchAsync(CommandTypes.PolicyUpdate);

        Assert.Equal(NackCodes.ExecFailed, SingleNack().GetProperty("code").GetString());
    }

    private Task DispatchAsync(string type, Guid? id = null, DateTimeOffset? ts = null, long? seq = null) =>
        _dispatcher.DispatchAsync(
            Envelopes.Create(type, payload: null, seq ?? ++_seq, ts ?? _time.GetUtcNow(), id),
            CancellationToken.None);

    private JsonElement SingleNack() => Assert.Single(_connection.OfType(MessageTypes.CommandNack)).Payload;

    private sealed class ScriptedHandler(string commandType) : ICommandHandler
    {
        public string CommandType { get; } = commandType;

        public bool IsRestrictive { get; set; }

        public CommandResult Result { get; set; } = CommandResult.Success();

        public bool AcknowledgeEarly { get; set; }

        public bool Throw { get; set; }

        public int Executions { get; private set; }

        public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
        {
            Executions++;

            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            if (AcknowledgeEarly)
            {
                await command.AcknowledgeEarlyAsync();
            }

            return Result;
        }
    }
}
