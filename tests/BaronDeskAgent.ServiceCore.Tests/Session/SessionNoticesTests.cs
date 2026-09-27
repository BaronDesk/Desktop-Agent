using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Ipc;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Tests.Session;

public sealed class SessionNoticesTests
{
    private static readonly Guid Active = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset ServerNow = new(2026, 9, 27, 21, 0, 0, TimeSpan.Zero);

    private static SessionNoticePayload Notice(string kind, Guid? sessionId = null, DateTimeOffset? endsAt = null, string? message = null) => new()
    {
        SessionId = sessionId ?? Active,
        Kind = kind,
        EndsAt = endsAt,
        Message = message
    };

    [Fact]
    public void Low_balance_countdown_comes_from_the_server_clock()
    {
        Assert.True(SessionNotices.TryCreate(Notice(SessionNoticeKinds.LowBalance, endsAt: ServerNow.AddMinutes(3)), Active, ServerNow, out var notice, out _));

        Assert.Equal(SessionNoticeKind.LowBalance, notice!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(3), notice.Remaining);
    }

    [Fact]
    public void A_deadline_already_past_counts_as_zero()
    {
        Assert.True(SessionNotices.TryCreate(Notice(SessionNoticeKinds.TimeLeft, endsAt: ServerNow.AddSeconds(-30)), Active, ServerNow, out var notice, out _));

        Assert.Equal(TimeSpan.Zero, notice!.Remaining);
    }

    [Fact]
    public void Notices_for_another_session_or_no_session_are_ignored()
    {
        Assert.False(SessionNotices.TryCreate(Notice(SessionNoticeKinds.LowBalance, sessionId: Guid.NewGuid()), Active, ServerNow, out _, out var reason));
        Assert.Equal("not the active session", reason);

        Assert.False(SessionNotices.TryCreate(Notice(SessionNoticeKinds.LowBalance), activeSessionId: null, ServerNow, out _, out _));
    }

    [Fact]
    public void Unknown_kinds_are_rejected()
    {
        Assert.False(SessionNotices.TryCreate(Notice("low_balance"), Active, ServerNow, out _, out var reason));
        Assert.Contains("unknown notice kind", reason);
    }

    [Fact]
    public void Clear_needs_no_deadline_and_long_messages_are_trimmed()
    {
        Assert.True(SessionNotices.TryCreate(Notice(SessionNoticeKinds.Clear), Active, ServerNow, out var clear, out _));
        Assert.Equal(SessionNoticeKind.Clear, clear!.Kind);
        Assert.Null(clear.Remaining);

        Assert.True(SessionNotices.TryCreate(Notice(SessionNoticeKinds.LowBalance, message: new string('x', 500)), Active, ServerNow, out var trimmed, out _));
        Assert.Equal(SessionNotices.MaxMessageLength, trimmed!.Message!.Length);
    }

    [Fact]
    public void Session_id_and_kind_are_required_on_the_wire()
    {
        var missingKind = JsonDocument.Parse($$"""{ "sessionId": "{{Active}}" }""").RootElement;

        Assert.False(CommandPayload.TryParseRequired(missingKind, AgentJsonContext.Default.SessionNoticePayload, out _, out _));
    }
}
