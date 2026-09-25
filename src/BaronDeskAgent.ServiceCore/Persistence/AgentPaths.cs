namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Files the agent keeps under <c>%ProgramData%\BaronDeskAgent</c> (locked down by <see cref="SecureDataDirectory"/>).
/// </summary>
public static class AgentPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BaronDeskAgent");

    public static string DataDirectory { get; } = Path.Combine(RootDirectory, "Data");

    public static string DatabaseFile { get; } = Path.Combine(DataDirectory, "agent.sqlite");

    /// <summary>The station credential, encrypted with DPAPI (LocalMachine). Never stored in SQLite.</summary>
    public static string CredentialFile { get; } = Path.Combine(DataDirectory, "station.credential");
}
