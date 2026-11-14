using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task NamingChoice_BlocksUnchosenDestinationAndPlacesOnlyTheSongWithoutChangingItsDate()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            NamingConfirmed = true,
            NamingScript = PromotionProfile.DefaultNamingScript,
            FakeLosslessCheck = false,
            ReplayGain = false,
            CoverMode = "none",
            LyricsMode = "off",
            MusicBrainzTags = false
        });
        var existing = Path.Combine(directory, "library", "music", "Kim Petras", "Detour (2026)", "06 - Brutalist.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "original library file");
        await SeedDestinationTrackAsync(existing);
        var song = DestinationSong();
        song.CanonicalRecordingId = recordingId;
        song.ExternalProvider = "deezer";
        song.ExternalId = "track";
        song.IsLocal = false;
        song.Codec = "pcm_s16le";
        song.Container = "wav";
        song.BitDepth = 16;
        song.SampleRate = 44100;
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { song });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(),
            It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var request = Request() with { ArtifactId = Guid.Empty, Unit = "song" };

        var preview = await batch.PreviewAsync(Context, request);
        Assert.True(preview.RequiresDestinationChoice);
        Assert.Empty(preview.DestinationPath);
        Assert.Equal(new[] { 2025, 2026 }, preview.DestinationChoices.Select(choice => choice.Year));
        var blocked = await Assert.ThrowsAsync<PromotionException>(() => batch.StartAsync(Context, request));
        Assert.Equal(PromotionFailure.DestinationChoiceRequired, blocked.Failure);
        var blockedRequest = await Assert.ThrowsAsync<PromotionException>(() => batch.StartOrRequestAsync(Context, request, requestApproval: true));
        Assert.Equal(PromotionFailure.DestinationChoiceRequired, blockedRequest.Failure);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.Jobs.AnyAsync(job => job.Type == LibraryPromotionService.JobType));
            Assert.Empty(await db.ManagedFiles.ToArrayAsync());
        }

        request = request with { NamingYear = 2026 };
        var selected = await batch.PreviewAsync(Context, request);
        Assert.False(selected.RequiresDestinationChoice);
        Assert.EndsWith("Kim Petras/Detour (2026)/03 - I Like Ur Look.wav", selected.DestinationPath.Replace('\\', '/'));
        var requestedExisting = await batch.StartOrRequestAsync(Context, request with { RequestKey = "existing-year" }, requestApproval: true);
        var requestedSource = await batch.StartOrRequestAsync(Context, request with { RequestKey = "source-year", NamingYear = 2025 }, requestApproval: true);
        Assert.NotNull(requestedExisting.Request);
        Assert.NotNull(requestedSource.Request);
        Assert.NotEqual(requestedExisting.Request.Id, requestedSource.Request.Id);
        await batch.CancelRequestAsync(Context, requestedExisting.Request.Id);
        await batch.CancelRequestAsync(Context, requestedSource.Request.Id);
        var run = await batch.StartAsync(Context, request);
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        var track = Assert.Single(placed.Tracks);
        Assert.Equal(selected.DestinationPath, track.DestinationPath);
        Assert.True(File.Exists(track.DestinationPath));
        Assert.Equal(2025, track.Song.Year);
        Assert.Equal("2025-10-14", track.Song.ReleaseDate);
        using var tagged = TagLib.File.Create(track.DestinationPath!);
        Assert.Equal(2025u, tagged.Tag.Year);
        Assert.Equal(3u, tagged.Tag.Track);
        Assert.Equal("original library file", await File.ReadAllTextAsync(existing));
        Assert.False(Directory.Exists(Path.Combine(directory, "library", "music", "Kim Petras", "Detour (2025)")));
        catalog.VerifyAll();
    }
}
