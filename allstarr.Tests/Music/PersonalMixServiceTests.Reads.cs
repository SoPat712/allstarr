using System.Data.Common;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using allstarr.Core.Protocols;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task StartWithEnoughSeedSongs_SkipsFavouriteReads()
    {
        await Seed();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        var probe = new MixReadProbe();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        taste = new(factory, tags, clock);
        service = NewService();

        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.Equal(0, probe.FavouriteReads);
        output.WriteLine($"Custom preview: favourite reads {probe.FavouriteReads}, total reads {probe.Reads}.");
    }

    [Fact]
    public async Task SavedMixList_ReadsOneDocumentWithoutHistoryReplayOrWrites()
    {
        await Seed();
        await service.GenerateAsync(owner);
        await ExternalLink();
        await ExternalLink(selection: "similar");
        await service.ListAsync(owner, "UTC");
        var probe = new MixReadProbe();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        taste = new(factory, tags, clock);
        service = NewService();
        var cards = await service.ListAsync(owner, "UTC");
        Assert.Equal(15, cards.Count);
        Assert.Equal(1, probe.MixDocuments);
        Assert.Equal(0, probe.HistoryReads);
        Assert.Equal(0, probe.FullHistoryReads);
        Assert.Equal(0, probe.Writes);
        output.WriteLine($"Saved mix list: documents {probe.MixDocuments}, history reads {probe.HistoryReads}, writes {probe.Writes}, total reads {probe.Reads}.");
    }

    private sealed class MixReadProbe : DbCommandInterceptor
    {
        public int MixDocuments, HistoryReads, FullHistoryReads, Writes, Reads, FavouriteReads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)) Reads++;
            else Writes++;
            if (sql.Contains("FROM \"listening_events\"", StringComparison.Ordinal)) HistoryReads++;
            if (sql.Contains("FROM \"favorite_states\"", StringComparison.Ordinal)) FavouriteReads++;
            if (sql.Contains("FROM \"listening_events\"", StringComparison.Ordinal) && sql.Contains("MusicBrainzFactsJson", StringComparison.Ordinal)) FullHistoryReads++;
            if (sql.Contains("PayloadJson", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(item => Equals(item.Value, "personal-mixes"))) MixDocuments++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
