namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>
/// The lock overlay shown by the LockUI helper in the user's session.
/// </summary>
public interface ILockScreen
{
    /// <summary>Shows the overlay and waits for the helper to confirm it is visible.</summary>
    Task<OverlayResult> ShowAsync(CancellationToken cancellationToken);

    /// <summary>Hides the overlay and waits for the helper to confirm it is hidden.</summary>
    Task<OverlayResult> HideAsync(CancellationToken cancellationToken);
}

public enum OverlayResult
{
    Confirmed,

    /// <summary>No LockUI helper is connected to the service, so nothing is shown on screen.</summary>
    HelperNotConnected,

    /// <summary>The helper is connected but did not confirm in time (or disconnected meanwhile).</summary>
    NotConfirmed
}

public static class OverlayResultExtensions
{
    /// <summary>Reason sent to the backend in a <c>command_nack</c> when a lock could not be confirmed.</summary>
    public static string DescribeLockFailure(this OverlayResult result) => result switch
    {
        OverlayResult.HelperNotConnected =>
            "The station is now locked, but the lock screen app (LockUI) is not running or not connected, so nothing covers the screen.",
        _ =>
            "The station is now locked, but the lock screen app did not confirm that the overlay is visible."
    };
}
