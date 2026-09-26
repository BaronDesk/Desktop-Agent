using System.Collections.Frozen;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// The only server → agent commands the agent executes (FROZEN allow-list, matched case-sensitively).
/// </summary>
public static class CommandTypes
{
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string Shutdown = "SHUTDOWN";
    public const string LaunchGame = "LAUNCH_GAME";
    public const string EndSession = "END_SESSION";
    public const string PolicyUpdate = "POLICY_UPDATE";

    // OPEN (skill §15 item 9): not in the frozen list yet, confirm with backend member C (see GameCatalogContracts).
    public const string CatalogUpdate = "CATALOG_UPDATE";

    public static readonly FrozenSet<string> All =
        new[] { Lock, Unlock, Shutdown, LaunchGame, EndSession, PolicyUpdate, CatalogUpdate }.ToFrozenSet(StringComparer.Ordinal);
}
