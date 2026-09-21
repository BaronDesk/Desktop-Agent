namespace BaronDesk.Shared.Contracts;

public static class CommandTypes
{
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string Shutdown = "SHUTDOWN";
    public const string LaunchGame = "LAUNCH_GAME";
    public const string EndSession = "END_SESSION";
    public const string PolicyUpdate = "POLICY_UPDATE";
}