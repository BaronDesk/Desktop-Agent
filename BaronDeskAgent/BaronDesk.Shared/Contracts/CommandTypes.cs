namespace BaronDesk.Shared.Contracts;

public static class CommandTypes
{
    public const string Lock = "lock";
    public const string Unlock = "unlock";
    public const string EndSession = "end_session";
    public const string LaunchGame = "launch_game";
    public const string StopGame = "stop_game";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
}