using System.Data.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task StartupCatalogCleanupDiscoversGroupsWithoutWriterAndCommitsEachGroup()
    {
        var actor = Actor(_userA);
        for (var group = 0; group < 2; group++)
        {
            var recording = (await _service.CreateRecordingAsync(actor, "startup-group-" + group)).Recording;
            await Link(recording.Id, Context(actor, "deezer"), "startup-first-" + group);
            await Link(recording.Id, Context(actor, "deezer"), "startup-second-" + group);
        }
        var probe = new CatalogStartupProbe();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(probe).Options);
        Assert.Equal(2, await new CatalogIdentityMaintenance(factory, _clock).RepairBatchAsync());
        Assert.Equal(1, probe.DiscoveryReads);
        Assert.Equal(0, probe.DiscoveryReadsInsideTransaction);
        Assert.Equal(2, probe.Writes);
        Assert.Equal(2, probe.Transactions.Count);
    }

    [Fact]
    public async Task StartupAutomaticRouteRepairReleasesWriterBetweenIdentities()
    {
        var actor = Actor(_userA);
        for (var index = 0; index < 3; index++)
        {
            var recording = (await _service.CreateRecordingAsync(actor, "automatic-startup-" + index)).Recording;
            await _service.LinkAsync(Context(actor, "deezer"), new(recording.Id, Track("deezer", "automatic-startup-" + index),
                ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "automatic-match", 1));
        }
        var probe = new CatalogStartupProbe();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(probe).Options);
        await new CatalogIdentityMaintenance(factory, _clock).RunAsync(default);
        Assert.Equal(3, probe.Transactions.Count);
        Assert.Equal(0, probe.Writes);
    }

    private sealed class CatalogStartupProbe : DbCommandInterceptor
    {
        public int DiscoveryReads;
        public int DiscoveryReadsInsideTransaction;
        public int Writes;
        public readonly HashSet<DbTransaction> Transactions = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.Transaction != null)
                Transactions.Add(command.Transaction);
            if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal))
            {
                DiscoveryReads++;
                if (command.Transaction != null) DiscoveryReadsInsideTransaction++;
            }
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)) Writes++;
            return ValueTask.FromResult(result);
        }
    }
}
