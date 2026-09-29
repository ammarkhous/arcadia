namespace Arcadia.Core.Games;

/// <summary>A short personal note in the gaming journal.</summary>
public sealed class JournalEntry
{
    public int Id { get; set; }
    public int? GameId { get; set; }
    public Game? Game { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsPinned { get; set; }
    public bool IsSample { get; set; }
}
