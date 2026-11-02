using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderCoverageRunnerTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestClock clock = new();
    private readonly Mock<IProtocolProviderGateway> gateway = new();
    private ProviderCheckServiceTests.Factory factory = null!;
    private readonly Guid admin = Guid.NewGuid();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new()
        {
            Id = admin,
            DisplayName = "Admin",
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            BackendPrincipalId = "admin",
            Enabled = true,
            IsAdmin = true
        });
        await db.SaveChangesAsync();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["has-it", "missing-it", "broken"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["kept", "words"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "has-it", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { ExternalProvider = "has-it", ExternalId = "song", Title = "Song", Artist = "Artist", Album = "Album" }]));
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "missing-it", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { ExternalProvider = "missing-it", ExternalId = "other", Title = "Songs About Something", Artist = "Artist", Album = "Album" }]));
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "broken", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Failure(new(ProviderErrorKind.TransientFailure)));
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private ProviderCoverageRunner Runner() => new(factory,
        new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance), gateway.Object, clock);

    private async Task Play(string title, int times, Guid? recording = null)
    {
        await using var db = factory.CreateDbContext();
        if (recording is { } id) db.CanonicalRecordings.Add(new() { Id = id, CreatedByUserId = admin });
        for (var index = 0; index < times; index++)
            db.ListeningEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = admin,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                OccurrenceKey = Guid.NewGuid().ToString("N"),
                State = ListeningEventState.Completed,
                ListenedAt = clock.GetUtcNow(),
                UpdatedAt = clock.GetUtcNow(),
                SourceKind = "import",
                TrackReference = title,
                Title = title,
                Artist = "Artist",
                Album = "Album",
                CanonicalRecordingId = recording
            });
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<string, string>> Recorded()
    {
        await using var db = factory.CreateDbContext();
        return (await db.AuditEvents.AsNoTracking().Where(row => row.Category == ProviderCoverageRunner.Category).ToArrayAsync())
            .ToDictionary(row => row.SubjectId!, row => row.Outcome!);
    }

    [Fact]
    public async Task RecordsWhatEachSourceHasAndSaysNothingForOneThatCannotAnswer()
    {
        await Play("Song", 3, Guid.NewGuid());
        gateway.Setup(item => item.GetRecordingLyricsAsync(It.IsAny<ProtocolExecutionContext>(), "words", It.IsAny<Guid>(),
                It.IsAny<ProviderLyricsFormat?>(), "Song", It.IsAny<IReadOnlyList<string>?>(), "Album", null, false, null))
            .ReturnsAsync((ProviderLyricsResult?)null);
        await Runner().RunAsync(default);
        var recorded = await Recorded();
        Assert.Equal("found", recorded["has-it/streaming"]);
        Assert.Equal("missing", recorded["missing-it/streaming"]);
        Assert.False(recorded.ContainsKey("broken/streaming"));
        Assert.Equal("missing", recorded["words/lyrics"]);
        Assert.False(recorded.ContainsKey("kept/lyrics"));
    }

    [Fact]
    public async Task SamplesTheMostPlayedSongsAndRotatesThroughThem()
    {
        for (var index = 0; index < ProviderCoverageRunner.SongsPerRun + 2; index++) await Play($"Song {index}", index + 1);
        var titles = new List<string>();
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "has-it", It.IsAny<string>(), 15))
            .Callback<ProtocolExecutionContext, string, string, int>((_, _, query, _) => titles.Add(query))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([]));
        await Runner().RunAsync(default);
        var first = titles.Distinct().Count();
        Assert.True(first >= ProviderCoverageRunner.SongsPerRun);
        titles.Clear();
        clock.Advance(TimeSpan.FromHours(6));
        await Runner().RunAsync(default);
        Assert.Contains(titles, query => query.Contains("Song 0", StringComparison.Ordinal) || query.Contains("Song 1 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNothingWithoutAnAdministratorOrAnyListening()
    {
        await Runner().RunAsync(default);
        Assert.Empty(await Recorded());
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
