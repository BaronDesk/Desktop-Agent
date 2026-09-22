namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>
/// The lock overlay shown by the LockUI helper in the user's session.
/// </summary>
public interface ILockScreen
{
    /// <summary>Shows the overlay. Returns true once the helper confirms it is visible.</summary>
    Task<bool> ShowAsync(CancellationToken cancellationToken);

    /// <summary>Hides the overlay. Returns true once the helper confirms it is hidden.</summary>
    Task<bool> HideAsync(CancellationToken cancellationToken);
}
