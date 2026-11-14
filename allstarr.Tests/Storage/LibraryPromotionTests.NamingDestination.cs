using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task DestinationNaming_OffersExistingYearAndExplicitSourceFolder()
    {
        var root = DestinationRoot();
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026)/01 - Brutalist.flac");
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026)/03 - I Like Ur Look.flac");
        var song = DestinationSong();
        var profile = new PromotionProfile { NamingScript = PromotionProfile.DefaultNamingScript };

        await using var db = factory.CreateDbContext();
        var result = await PromotionDestinationNaming.ResolveAsync(db, Context, await libraryAccess.ResolveAsync(Context),
            root, profile, song, null, default);

        Assert.True(result.RequiresChoice);
        Assert.Null(result.Year);
        Assert.Equal([2025, 2026], result.Choices.Select(choice => choice.Year));
        Assert.Equal("Kim Petras/Detour (2025)", Assert.Single(result.Choices, choice => choice.Year == 2025).RelativeFolder);
        Assert.Equal("Kim Petras/Detour (2026)", Assert.Single(result.Choices, choice => choice.Year == 2026).RelativeFolder);
    }

    [Fact]
    public async Task DestinationNaming_AcceptsSelectedExistingOrSourceYearAndRejectsArbitraryYear()
    {
        var root = DestinationRoot();
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026)/03 - I Like Ur Look.flac");
        var profile = new PromotionProfile { NamingScript = PromotionProfile.DefaultNamingScript };
        await using var db = factory.CreateDbContext();
        var access = await libraryAccess.ResolveAsync(Context);

        var existing = await PromotionDestinationNaming.ResolveAsync(db, Context, access, root, profile, DestinationSong(), 2026, default);
        Assert.False(existing.RequiresChoice);
        Assert.Equal(2026, existing.Year);

        var source = await PromotionDestinationNaming.ResolveAsync(db, Context, access, root, profile, DestinationSong(), 2025, default);
        Assert.False(source.RequiresChoice);
        Assert.Equal(2025, source.Year);

        var error = await Assert.ThrowsAsync<PromotionException>(() =>
            PromotionDestinationNaming.ResolveAsync(db, Context, access, root, profile, DestinationSong(), 2024, default));
        Assert.Equal(PromotionFailure.NamingMetadataMissing, error.Failure);
    }

    [Fact]
    public async Task DestinationNaming_IgnoresSiblingAndDeniedLibraryRows()
    {
        var root = DestinationRoot();
        await SeedDestinationTrackAsync("/backend/music-other/Kim Petras/Detour (2026)/03 - I Like Ur Look.flac");
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026)/03 - I Like Ur Look.flac", "other");
        var profile = new PromotionProfile { NamingScript = PromotionProfile.DefaultNamingScript };

        await using var db = factory.CreateDbContext();
        var result = await PromotionDestinationNaming.ResolveAsync(db, Context, await libraryAccess.ResolveAsync(Context),
            root, profile, DestinationSong(), null, default);

        Assert.False(result.RequiresChoice);
        Assert.Equal(2025, result.Year);
        Assert.Empty(result.Choices);
    }

    [Fact]
    public async Task DestinationNaming_BlocksAnExistingAlbumLayoutThatYearCannotReproduce()
    {
        var root = DestinationRoot();
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Archive 2026/03 - I Like Ur Look.flac");
        var profile = new PromotionProfile { NamingScript = PromotionProfile.DefaultNamingScript };

        await using var db = factory.CreateDbContext();
        var access = await libraryAccess.ResolveAsync(Context);
        var error = await Assert.ThrowsAsync<PromotionException>(() =>
            PromotionDestinationNaming.ResolveAsync(db, Context, access, root, profile, DestinationSong(), null, default));
        Assert.Equal(PromotionFailure.ExistingAlbumLayoutMismatch, error.Failure);
    }

    [Fact]
    public async Task DestinationNaming_KeepsAnExplicitExistingChoiceWhenBothYearFoldersExist()
    {
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2025)/01 - Song.flac");
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026)/02 - Song.flac");
        await using var db = factory.CreateDbContext();
        var result = await PromotionDestinationNaming.ResolveAsync(db, Context, await libraryAccess.ResolveAsync(Context),
            DestinationRoot(), new PromotionProfile(), DestinationSong(), 2026, default);
        Assert.False(result.RequiresChoice);
        Assert.Equal(2026, result.Year);
    }

    [Fact]
    public async Task DestinationNaming_DoesNotPretendAYearChoiceChangesTheReleaseDate()
    {
        await SeedDestinationTrackAsync("/backend/music/Kim Petras/Detour (2026-10-14)/01 - Song.flac");
        await using var db = factory.CreateDbContext();
        var access = await libraryAccess.ResolveAsync(Context);
        var error = await Assert.ThrowsAsync<PromotionException>(() => PromotionDestinationNaming.ResolveAsync(db, Context, access,
            DestinationRoot(), new PromotionProfile { NamingScript = @"%artist%/%album% \(%date%\)/%title%.%_extension%" },
            DestinationSong(), 2026, default));
        Assert.Equal(PromotionFailure.ExistingAlbumLayoutMismatch, error.Failure);
    }

    private PromotionRoot DestinationRoot() =>
        new(rootId, "Music", Path.Combine(directory, "library", "music"), "/backend/music", "music");

    private static Song DestinationSong() => new()
    {
        Title = "I Like Ur Look",
        Artist = "Kim Petras",
        AlbumArtist = "Kim Petras",
        Album = "Detour",
        Track = 3,
        Year = 2025,
        ReleaseDate = "2025-10-14",
        Container = "flac"
    };

    private async Task SeedDestinationTrackAsync(string path, string library = "music")
    {
        await using var db = factory.CreateDbContext();
        db.LibraryTracks.Add(new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendLibraryId = library,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = Guid.CreateVersion7().ToString("N"),
            FilePath = path,
            Title = "I Like Ur Look",
            Artist = "Kim Petras",
            Album = "Detour",
            AlbumArtist = "Kim Petras",
            IndexedAt = DateTimeOffset.UtcNow,
            SourceModifiedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
