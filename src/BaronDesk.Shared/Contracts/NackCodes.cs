namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>command_nack.code</c> values.
/// </summary>
public static class NackCodes
{
    public const string UnknownType = "UNKNOWN_TYPE";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string ExecFailed = "EXEC_FAILED";
    public const string Stale = "STALE";
}
