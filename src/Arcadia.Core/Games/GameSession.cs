namespace Arcadia.Core.Games;

/// <summary>A logged play session. Sessions are the source of truth for recent activity.</summary>
public sealed class GameSession
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game? Game { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public int DurationMinutes { get; set; }
    public string? Note { get; set; }
    public bool IsSample { get; set; }
    /// <summary>True when Arcadia recorded the session by watching the game's process.</summary>
    public bool IsAutomatic { get; set; }

    public double Hours => DurationMinutes / 60.0;
}
