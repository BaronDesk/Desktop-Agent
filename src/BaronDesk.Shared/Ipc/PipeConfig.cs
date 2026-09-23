namespace BaronDesk.Shared.Ipc;

public static class PipeConfig
{
    public const string Name = "BaronDeskAgentPipe";

    /// <summary>Upper bound for one newline-delimited IPC message; longer input drops the connection.</summary>
    public const int MaxMessageChars = 4096;
}
