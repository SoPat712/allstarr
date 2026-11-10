using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class TasteServiceTests
{
    [Fact]
    public async Task Profile_ColdReadReturnsStoredTasteWhileRecomputationIsBlocked()
    {
        await using var fixture = await Fixture.Create();
        await SeedProfileHistory(fixture);
        var saved = await fixture.Service.GetAsync(fixture.User);
        var gate = new ProfileTagGate();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(fixture.Factory.Options).AddInterceptors(gate).Options);
        var service = new TasteService(factory, new(factory, fixture.Clock), fixture.Clock);
        try
        {
            var profile = await service.GetCachedAsync(fixture.User).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(saved.Usually.Artists, profile.Usually.Artists);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, gate.Reads);
        }
        finally
        {
            gate.Release.TrySetResult();
            await service.RefreshProfileAsync(fixture.User);
        }
    }

    [Fact]
    public async Task Profile_ChangedChoiceRefreshesStoredTasteInTheBackground()
    {
        await using var fixture = await Fixture.Create();
        await SeedProfileHistory(fixture);
        await fixture.Service.GetAsync(fixture.User);
        var gate = new ProfileTagGate();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(fixture.Factory.Options).AddInterceptors(gate).Options);
        var service = new TasteService(factory, new(factory, fixture.Clock), fixture.Clock);
        try
        {
            await service.PutNudgeAsync(fixture.User, new("tag", "pop", "more"));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.PutNudgeAsync(fixture.User, new("tag", "pop", "less"));
            var profile = await service.GetCachedAsync(fixture.User).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("pop", Assert.Single(profile.Nudges).Name);
            Assert.Equal("less", Assert.Single(profile.Nudges).Mode);
            Assert.Equal("Imported artist", Assert.Single(profile.Usually.Artists).Name);
        }
        finally
        {
            gate.Release.TrySetResult();
            await service.RefreshProfileAsync(fixture.User);
        }
        await using var db = fixture.Factory.CreateDbContext();
        var row = await db.TasteProfiles.SingleAsync(item => item.OwnerUserId == fixture.User);
        var document = JsonSerializer.Deserialize<TasteDocument>(row.PayloadJson)!;
        Assert.Equal(row.Revision, document.Snapshot!.Revision);
        Assert.Equal("pop", Assert.Single(document.Snapshot.Profile.Nudges).Name);
        Assert.Equal("less", Assert.Single(document.Snapshot.Profile.Nudges).Mode);
        Assert.Equal(1, document.Snapshot.Plays);
    }

    private static async Task SeedProfileHistory(Fixture fixture)
    {
        var song = await fixture.Song("pop");
        await using var db = fixture.Factory.CreateDbContext();
        db.ListeningEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = fixture.User,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            OccurrenceKey = "imported-profile",
            SourceKind = "import",
            State = ListeningEventState.Completed,
            CanonicalRecordingId = song.CanonicalRecordingId,
            Artist = "Imported artist",
            ListenedAt = fixture.Clock.UtcNow,
            UpdatedAt = fixture.Clock.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed class ProfileTagGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"shared_tag_profiles\"", StringComparison.Ordinal) && !Release.Task.IsCompleted)
            {
                Interlocked.Increment(ref Reads);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
