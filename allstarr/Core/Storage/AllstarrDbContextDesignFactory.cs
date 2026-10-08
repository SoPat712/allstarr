using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace allstarr.Core.Storage;

public sealed class AllstarrDbContextDesignFactory : IDesignTimeDbContextFactory<AllstarrDbContext>
{
    public AllstarrDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_DESIGN_CONNECTION_STRING")
            ?? "Data Source=design.db";
        var options = new DbContextOptionsBuilder<AllstarrDbContext>();
        options.UseSqlite(connectionString);

        return new AllstarrDbContext(options.Options);
    }
}
