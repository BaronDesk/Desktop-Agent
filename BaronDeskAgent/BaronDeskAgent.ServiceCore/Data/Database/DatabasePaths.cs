namespace BaronDeskAgent.ServiceCore.Data.Database;

public static class DatabasePaths
{
    private const string ApplicationFolder =
        "BaronDeskAgent";

    public static string RootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            ApplicationFolder);

    public static string DataDirectory =>
        Path.Combine(
            RootDirectory,
            "Data");

    public static string DatabaseFile =>
        Path.Combine(
            DataDirectory,
            "agent.sqlite");
}