using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace allstarr.Core.Storage;

public sealed class AllstarrDbContextDesignFactory : IDesignTimeDbContextFactory<AllstarrDbContext>
{
    public AllstarrDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("ALLSTARR_DESIGN_PROVIDER") ?? "Sqlite";
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_DESIGN_CONNECTION_STRING")
            ?? "Data Source=allstarr-design.db";
        var options = new DbContextOptionsBuilder<AllstarrDbContext>();
        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            options.UseNpgsql(connectionString);
        }
        else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(connectionString);
        }
        else
        {
            throw new InvalidOperationException("ALLSTARR_DESIGN_PROVIDER must be Postgres or Sqlite.");
        }

        return new AllstarrDbContext(options.Options);
    }
}
