using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PseSignalDesk.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> so migrations can be generated without booting the web
/// host. The runtime connection string comes from appsettings/WebApi instead.
/// </summary>
public sealed class PseSignalDbContextFactory : IDesignTimeDbContextFactory<PseSignalDbContext>
{
    public PseSignalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PseSignalDbContext>()
            .UseSqlite("Data Source=pse_signals.db")
            .Options;

        return new PseSignalDbContext(options);
    }
}
