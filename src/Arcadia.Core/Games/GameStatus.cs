namespace Arcadia.Core.Games;

/// <summary>Where a game sits in the player's personal library lifecycle.</summary>
public enum GameStatus
{
    Backlog = 0,
    Playing = 1,
    Completed = 2,
    Abandoned = 3,
}

public enum BacklogPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}
