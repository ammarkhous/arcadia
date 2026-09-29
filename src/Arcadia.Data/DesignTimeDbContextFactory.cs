using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Arcadia.Data;

/// <summary>Used only by `dotnet ef` tooling to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ArcadiaDbContext>
{
    public ArcadiaDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "arcadia-design.db");
        return new ArcadiaDbContext(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path}").Options);
    }
}
