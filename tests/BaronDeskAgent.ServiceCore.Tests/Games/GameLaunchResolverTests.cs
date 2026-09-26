using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Games;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class GameLaunchResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("barondesk-games-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Exe_resolves_to_the_catalog_path_arguments_and_working_directory()
    {
        var exe = CreateFile("Games", "game.exe");
        var game = Game(GameLaunchTypes.Exe, exe, arguments: "-windowed", workingDirectory: _root);

        var command = Resolve(game).Command;

        Assert.Equal(new LaunchCommand(exe, "-windowed", _root), command);
    }

    [Fact]
    public void Missing_exe_is_not_installed_and_the_reason_has_no_path()
    {
        var missing = Path.Combine(_root, "Games", "missing.exe");

        var resolution = Resolve(Game(GameLaunchTypes.Exe, missing));

        Assert.False(resolution.IsInstalled);
        Assert.DoesNotContain(_root, resolution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exe_outside_the_allowed_game_folders_is_refused()
    {
        var allowed = Path.Combine(_root, "Games");
        var inside = CreateFile("Games", "ok.exe");
        var outside = CreateFile("Other", "bad.exe");
        var sibling = CreateFile("Games2", "bad.exe");   // shares the "Games" prefix
        var traversal = Path.Combine(allowed, "..", "Other", "bad.exe");

        var resolver = Resolver(allowed);

        Assert.True(resolver.Resolve(Game(GameLaunchTypes.Exe, inside), GameLibraries.None).IsInstalled);
        Assert.False(resolver.Resolve(Game(GameLaunchTypes.Exe, outside), GameLibraries.None).IsInstalled);
        Assert.False(resolver.Resolve(Game(GameLaunchTypes.Exe, sibling), GameLibraries.None).IsInstalled);
        Assert.False(resolver.Resolve(Game(GameLaunchTypes.Exe, traversal), GameLibraries.None).IsInstalled);
    }

    [Fact]
    public void Steam_game_launches_through_steam_exe_with_applaunch()
    {
        var steam = CreateFile("Steam", "steam.exe");
        var library = Path.Combine(_root, "SteamLibrary");
        CreateFile(Path.Combine("SteamLibrary", "steamapps"), "appmanifest_730.acf");
        var libraries = GameLibraries.None with
        {
            SteamExecutable = steam,
            SteamLibraryDirectories = [Path.Combine(_root, "Steam"), library]
        };

        var command = Resolve(Game(GameLaunchTypes.Steam, "730", arguments: "-novid"), libraries).Command;

        Assert.Equal(new LaunchCommand(steam, "-applaunch 730 -novid", null), command);
    }

    [Fact]
    public void Steam_game_without_a_manifest_or_without_steam_is_not_installed()
    {
        var steam = CreateFile("Steam", "steam.exe");
        var withSteam = GameLibraries.None with { SteamExecutable = steam, SteamLibraryDirectories = [Path.Combine(_root, "Steam")] };

        Assert.False(Resolve(Game(GameLaunchTypes.Steam, "730"), withSteam).IsInstalled);
        Assert.False(Resolve(Game(GameLaunchTypes.Steam, "730"), GameLibraries.None).IsInstalled);
    }

    [Fact]
    public void Epic_game_launches_through_the_launcher_uri()
    {
        var launcher = CreateFile("Epic", "EpicGamesLauncher.exe");
        var install = Directory.CreateDirectory(Path.Combine(_root, "Fortnite")).FullName;
        var libraries = GameLibraries.None with
        {
            EpicLauncherExecutable = launcher,
            EpicInstallLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Fortnite"] = install }
        };

        var command = Resolve(Game(GameLaunchTypes.Epic, "fortnite"), libraries).Command;

        Assert.Equal(new LaunchCommand(launcher, "\"com.epicgames.launcher://apps/fortnite?action=launch&silent=true\"", null), command);
    }

    [Fact]
    public void Epic_game_whose_install_folder_is_gone_is_not_installed()
    {
        var launcher = CreateFile("Epic", "EpicGamesLauncher.exe");
        var libraries = GameLibraries.None with
        {
            EpicLauncherExecutable = launcher,
            EpicInstallLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Fortnite"] = Path.Combine(_root, "gone") }
        };

        Assert.False(Resolve(Game(GameLaunchTypes.Epic, "Fortnite"), libraries).IsInstalled);
    }

    private LaunchResolution Resolve(GameCatalogEntity game, GameLibraries? libraries = null) =>
        Resolver().Resolve(game, libraries ?? GameLibraries.None);

    private static GameLaunchResolver Resolver(params string[] allowedDirectories) =>
        new(Options.Create(new AgentOptions { AllowedGameDirectories = allowedDirectories }));

    private static GameCatalogEntity Game(string launchType, string target, string? arguments = null, string? workingDirectory = null) => new()
    {
        GameId = "g1",
        Name = "Game",
        LaunchType = launchType,
        Target = target,
        LaunchArguments = arguments,
        WorkingDirectory = workingDirectory
    };

    private string CreateFile(string folder, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
