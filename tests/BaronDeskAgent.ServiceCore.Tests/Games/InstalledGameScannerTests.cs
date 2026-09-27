using System.Text;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Games;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class InstalledGameScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("barondesk-scan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Acf(string appId, string name, int stateFlags) =>
        $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"Universe"		"1"
        	"name"		"{{name}}"
        	"StateFlags"		"{{stateFlags}}"
        	"installdir"		"{{name}}"
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void A_fully_installed_steam_app_is_a_game()
    {
        var game = InstalledGameScanner.ParseSteamManifest(Acf("730", "Counter-Strike 2", 4));

        Assert.Equal(new InstalledGame(GameLaunchTypes.Steam, "730", "Counter-Strike 2", null), game);
    }

    [Theory]
    [InlineData("730", "Counter-Strike 2", 1026)]                    // update required, not installed yet (no bit 4)
    [InlineData("228980", "Steamworks Common Redistributables", 4)]  // redistributable
    [InlineData("1493710", "Proton Experimental", 4)]                // compatibility tool
    [InlineData("abc", "Broken", 4)]                                 // app id must be digits
    public void Partial_installs_tools_and_broken_manifests_are_skipped(string appId, string name, int stateFlags)
    {
        Assert.Null(InstalledGameScanner.ParseSteamManifest(Acf(appId, name, stateFlags)));
    }

    [Fact]
    public void Steam_libraries_are_scanned_and_unreadable_folders_ignored()
    {
        var library = Path.Combine(_root, "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_570.acf"), Acf("570", "Dota 2", 4));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1.acf"), "not a manifest");

        var games = InstalledGameScanner.ScanSteam([library, Path.Combine(_root, "missing")], NullLogger.Instance).ToList();

        Assert.Equal("570", Assert.Single(games).Target);
    }

    [Fact]
    public void Epic_games_suggest_their_launch_executable_as_process_name()
    {
        const string item = """
            { "AppName": "Fortnite", "DisplayName": "Fortnite", "InstallLocation": "C:\\Epic\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
              "AppCategories": ["public", "games", "applications"], "bIsIncompleteInstall": false }
            """;

        var game = InstalledGameScanner.ParseEpicManifest(Encoding.UTF8.GetBytes(item));

        Assert.Equal(new InstalledGame(GameLaunchTypes.Epic, "Fortnite", "Fortnite", "FortniteLauncher.exe"), game);
    }

    [Theory]
    [InlineData("""{ "AppName": "Half", "DisplayName": "Half", "bIsIncompleteInstall": true }""")]
    [InlineData("""{ "AppName": "UE_5.4", "DisplayName": "Unreal Engine", "AppCategories": ["engines"] }""")]
    [InlineData("""{ "DisplayName": "No app name" }""")]
    public void Incomplete_installs_and_non_games_are_skipped_on_epic(string item)
    {
        Assert.Null(InstalledGameScanner.ParseEpicManifest(Encoding.UTF8.GetBytes(item)));
    }

    [Fact]
    public void Epic_scan_skips_corrupt_manifests()
    {
        File.WriteAllText(Path.Combine(_root, "ok.item"), """{ "AppName": "Rocket", "DisplayName": "Rocket League", "AppCategories": ["games"] }""");
        File.WriteAllText(Path.Combine(_root, "bad.item"), "{ not json");

        var game = Assert.Single(InstalledGameScanner.ScanEpic(_root, NullLogger.Instance));

        Assert.Equal("Rocket League", game.Name);
    }

    [Fact]
    public void Games_already_in_the_catalog_are_marked()
    {
        InstalledGame[] installed =
        [
            new(GameLaunchTypes.Steam, "730", "Counter-Strike 2", null),
            new(GameLaunchTypes.Epic, "Fortnite", "Fortnite", "FortniteLauncher.exe")
        ];
        GameCatalogEntity[] catalog =
        [
            new() { GameId = "cs2", Name = "CS2", LaunchType = GameLaunchTypes.Steam, Target = "730" },
            new() { GameId = "fn", Name = "Fortnite", LaunchType = GameLaunchTypes.Steam, Target = "Fortnite" } // wrong launcher
        ];

        var result = InstalledGameScanner.CompareWithCatalog(installed, catalog);

        Assert.True(result.Single(g => g.Target == "730").InCatalog);
        Assert.False(result.Single(g => g.Target == "Fortnite").InCatalog);
    }
}
