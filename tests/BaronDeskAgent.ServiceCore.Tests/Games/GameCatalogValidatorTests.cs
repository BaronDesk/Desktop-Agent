using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Games;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class GameCatalogValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Launch_type_defaults_to_exe_and_fields_are_trimmed()
    {
        var entity = Valid(new CatalogGame { GameId = " g1 ", Name = " Game ", Target = @" D:\Games\game.exe " });

        Assert.Equal("g1", entity.GameId);
        Assert.Equal("Game", entity.Name);
        Assert.Equal(GameLaunchTypes.Exe, entity.LaunchType);
        Assert.Equal(@"D:\Games\game.exe", entity.Target);
    }

    [Fact]
    public void Name_falls_back_to_the_game_id()
    {
        Assert.Equal("g1", Valid(new CatalogGame { GameId = "g1", Target = @"D:\Games\game.exe" }).Name);
    }

    [Theory]
    [InlineData("STEAM", "steam")]
    [InlineData("Epic", "epic")]
    public void Launch_type_is_case_insensitive(string launchType, string expected)
    {
        var target = expected == "steam" ? "730" : "Fortnite";
        Assert.Equal(expected, Valid(new CatalogGame { GameId = "g", LaunchType = launchType, Target = target }).LaunchType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Game_id_is_required(string? gameId)
    {
        Invalid(new CatalogGame { GameId = gameId, Target = @"D:\Games\game.exe" });
    }

    [Fact]
    public void Oversized_game_id_is_rejected()
    {
        Invalid(new CatalogGame { GameId = new string('a', 129), Target = @"D:\Games\game.exe" });
    }

    [Fact]
    public void Unknown_launch_type_is_rejected()
    {
        Invalid(new CatalogGame { GameId = "g", LaunchType = "uplay", Target = "123" });
    }

    [Theory]
    [InlineData(@"game.exe")]                       // relative
    [InlineData(@"..\game.exe")]
    [InlineData(@"D:\Games\game.bat")]              // not an .exe
    [InlineData(@"D:\Games\game.exe"" --evil")]     // quote would break out of the command line
    [InlineData(@"C:game.exe")]                     // drive-relative
    [InlineData("")]
    public void Exe_target_must_be_a_fully_qualified_exe(string target)
    {
        Invalid(new CatalogGame { GameId = "g", Target = target });
    }

    [Fact]
    public void Unc_paths_are_allowed_for_games_on_a_network_share()
    {
        Valid(new CatalogGame { GameId = "g", Target = @"\\nas\games\game.exe" });
    }

    [Theory]
    [InlineData("730a")]
    [InlineData("-1")]
    [InlineData("12345678901")]
    [InlineData("730 -console")]
    public void Steam_target_must_be_an_app_id(string target)
    {
        Invalid(new CatalogGame { GameId = "g", LaunchType = "steam", Target = target });
    }

    [Theory]
    [InlineData("Fort nite")]
    [InlineData("Fortnite?action=uninstall")]
    [InlineData("../x")]
    [InlineData("Fortnite\"")]
    public void Epic_target_must_be_a_plain_app_name(string target)
    {
        Invalid(new CatalogGame { GameId = "g", LaunchType = "epic", Target = target });
    }

    [Fact]
    public void Arguments_with_control_characters_are_rejected()
    {
        Invalid(new CatalogGame { GameId = "g", Target = @"D:\Games\game.exe", Arguments = "-a\r\n-b" });
    }

    [Fact]
    public void Epic_ignores_arguments_and_only_exe_keeps_a_working_directory()
    {
        var epic = Valid(new CatalogGame { GameId = "g", LaunchType = "epic", Target = "Fortnite", Arguments = "-x", WorkingDirectory = @"D:\x" });
        var steam = Valid(new CatalogGame { GameId = "g", LaunchType = "steam", Target = "730", Arguments = "-novid", WorkingDirectory = @"D:\x" });

        Assert.Null(epic.LaunchArguments);
        Assert.Null(epic.WorkingDirectory);
        Assert.Equal("-novid", steam.LaunchArguments);
        Assert.Null(steam.WorkingDirectory);
    }

    [Fact]
    public void Relative_working_directory_is_rejected()
    {
        Invalid(new CatalogGame { GameId = "g", Target = @"D:\Games\game.exe", WorkingDirectory = @"Games" });
    }

    [Theory]
    [InlineData("cs2.exe", "cs2")]
    [InlineData("CS2.EXE", "CS2")]
    [InlineData(" cs2 ", "cs2")]
    public void Process_name_is_normalized_without_extension(string processName, string expected)
    {
        Assert.Equal(expected, Valid(new CatalogGame { GameId = "g", Target = @"D:\Games\game.exe", ProcessName = processName }).ProcessName);
    }

    [Theory]
    [InlineData(@"D:\Games\cs2.exe")]
    [InlineData("a/b")]
    [InlineData(".exe")]
    [InlineData("explorer.exe")]
    [InlineData("steam")]
    [InlineData("BaronDesk.LockUI")]
    public void Paths_and_reserved_process_names_are_rejected(string processName)
    {
        Invalid(new CatalogGame { GameId = "g", Target = @"D:\Games\game.exe", ProcessName = processName });
    }

    private static GameCatalogEntity Valid(CatalogGame game)
    {
        Assert.True(GameCatalogValidator.TryCreate(game, Now, out var entity, out var error), error);
        return entity;
    }

    private static void Invalid(CatalogGame game)
    {
        Assert.False(GameCatalogValidator.TryCreate(game, Now, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
