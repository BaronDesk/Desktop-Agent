using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Ipc;

namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>A notice ready for the lock screen helper. <see cref="Remaining"/> is already on the local monotonic scale.</summary>
public sealed record SessionNotice(SessionNoticeKind Kind, TimeSpan? Remaining, string? Message);

/// <summary>Shows in-session notices to the gamer (implemented by the pipe server, drawn by LockUI).</summary>
public interface ISessionNotifier
{
    Task ShowNoticeAsync(SessionNotice notice, CancellationToken cancellationToken);
}

public static class SessionNotices
{
    public const int MaxMessageLength = 200;

    /// <summary>
    /// Validates a <c>session_notice</c> against the active session and converts <c>endsAt</c> (server clock)
    /// into a remaining duration, so a drifted station clock cannot shift the countdown.
    /// </summary>
    public static bool TryCreate(
        SessionNoticePayload payload,
        Guid? activeSessionId,
        DateTimeOffset serverNow,
        out SessionNotice? notice,
        out string? reason)
    {
        notice = null;

        SessionNoticeKind? kind = payload.Kind switch
        {
            SessionNoticeKinds.LowBalance => SessionNoticeKind.LowBalance,
            SessionNoticeKinds.TimeLeft => SessionNoticeKind.TimeLeft,
            SessionNoticeKinds.Clear => SessionNoticeKind.Clear,
            _ => null
        };
        if (kind is null)
        {
            reason = $"unknown notice kind '{payload.Kind}'";
            return false;
        }

        if (activeSessionId != payload.SessionId)
        {
            reason = "not the active session";
            return false;
        }

        TimeSpan? remaining = payload.EndsAt is { } endsAt
            ? TimeSpan.FromTicks(Math.Max(0, (endsAt - serverNow).Ticks))
            : null;

        var message = string.IsNullOrWhiteSpace(payload.Message) ? null : payload.Message.Trim();
        if (message is { Length: > MaxMessageLength })
        {
            message = message[..MaxMessageLength];
        }

        notice = new SessionNotice(kind.Value, remaining, message);
        reason = null;
        return true;
    }
}
