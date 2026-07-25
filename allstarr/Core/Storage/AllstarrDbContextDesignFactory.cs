using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace allstarr.Core.Storage;

public sealed class AllstarrDbContextDesignFactory : IDesignTimeDbContextFactory<AllstarrDbContext>
{
    public AllstarrDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_DESIGN_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=allstarr;Username=allstarr";
        var options = new DbContextOptionsBuilder<AllstarrDbContext>();
        options.UseNpgsql(connectionString);

        return new AllstarrDbContext(options.Options);
    }
}
