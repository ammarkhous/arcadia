namespace Arcadia.Core.Games;

public interface IGameLibraryService
{
    Task<HomeDashboardSnapshot> GetHomeDashboardAsync(CancellationToken cancellationToken = default);
}
