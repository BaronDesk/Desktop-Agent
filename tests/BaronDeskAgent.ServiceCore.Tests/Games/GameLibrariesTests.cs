using BaronDeskAgent.ServiceCore.Games;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class GameLibrariesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("barondesk-libraries-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Steam_library_paths_are_read_and_unescaped()
    {
        const string vdf =
            """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"0"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            	"2"
            	{
            		"path"		"relative\\ignored"
            	}
            }
            """;

        var paths = WindowsGameLibraryLocator.ParseSteamLibraryPaths(vdf).ToList();

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], paths);
    }

    [Fact]
    public void Steam_install_folder_is_always_a_library_and_duplicates_are_dropped()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_root, "Steam")).FullName;
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{steam.Replace(@"\", @"\\").ToUpperInvariant()}\" }} \"1\" {{ \"path\" \"E:\\\\Games\" }} }}");

        var libraries = WindowsGameLibraryLocator.ReadSteamLibraries(steam, NullLogger.Instance);

        Assert.Equal([steam, @"E:\Games"], libraries);
    }

    [Fact]
    public void Epic_manifests_map_app_names_to_install_locations_and_skip_bad_files()
    {
        File.WriteAllText(Path.Combine(_root, "a.item"), """{ "AppName": "Fortnite", "InstallLocation": "D:\\Epic\\Fortnite", "DisplayName": "Fortnite" }""");
        File.WriteAllText(Path.Combine(_root, "b.item"), "{ not json");
        File.WriteAllText(Path.Combine(_root, "c.item"), """{ "AppName": "NoLocation" }""");
        File.WriteAllText(Path.Combine(_root, "d.txt"), """{ "AppName": "Ignored", "InstallLocation": "D:\\x" }""");

        var installs = WindowsGameLibraryLocator.ReadEpicManifests(_root, NullLogger.Instance);

        Assert.Single(installs);
        Assert.Equal(@"D:\Epic\Fortnite", installs["fortnite"]);
    }

    [Fact]
    public void Missing_epic_manifest_folder_means_no_games()
    {
        Assert.Empty(WindowsGameLibraryLocator.ReadEpicManifests(Path.Combine(_root, "missing"), NullLogger.Instance));
    }

    [Theory]
    [InlineData("\"C:\\Epic\\EpicGamesLauncher.exe\" %1", @"C:\Epic\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Epic\Launcher.exe %1", @"C:\Epic\Launcher.exe")]
    [InlineData("\"relative.exe\" %1", null)]
    [InlineData("\"C:\\unterminated.exe %1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Shell_command_executable_is_parsed(string? command, string? expected)
    {
        Assert.Equal(expected, WindowsGameLibraryLocator.ParseCommandExecutable(command));
    }
}
