namespace BaronDeskAgent.ServiceCore.Data.Entities;

public sealed class GameCatalogEntity
{
    public required string GameId { get; set; }

    public required string Name { get; set; }

    public required string ExecutablePath { get; set; }

    public string? LaunchArguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
