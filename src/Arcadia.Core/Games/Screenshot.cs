namespace Arcadia.Core.Games;

/// <summary>A captured moment from a game, optionally annotated like a journal page.</summary>
public sealed class Screenshot
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game? Game { get; set; }
    public string? ImagePath { get; set; }
    public string? ThumbnailPath { get; set; }
    /// <summary>Original file an import came from (used to avoid importing the same capture twice).</summary>
    public string? SourcePath { get; set; }
    public required string Title { get; set; }
    public string? Location { get; set; }
    public string? Note { get; set; }
    public string Tags { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; }
    public string ArtworkTheme { get; set; } = "mountains-dusk";
    public int ArtworkSeed { get; set; }
    public bool IsSample { get; set; }
}
