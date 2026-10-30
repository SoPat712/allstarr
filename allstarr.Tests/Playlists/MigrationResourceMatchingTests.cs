using allstarr.Core.Capabilities;
using allstarr.Core.Migrations;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class MigrationResourceMatchingTests
{
    [Theory]
    [InlineData("Release", false, "012345678901", 12, true)]
    [InlineData("Release (Deluxe)", false, "012345678901", 12, false)]
    [InlineData("Release", true, "012345678901", 12, false)]
    [InlineData("Release", false, "012345678902", 12, false)]
    [InlineData("Release", false, "012345678901", 16, false)]
    public void AlbumRequiresSameEditionAndKnownReleaseEvidence(string title, bool explicitAlbum, string upc, int count, bool accepted)
    {
        var source = new ProviderLibraryResource("source", "Release", ["Artist"], [], "2024-01-01", 12, null, "012345678901", false);
        var candidate = new Album
        {
            Id = "ext-deezer-album-one",
            Title = title,
            Artist = "Artist",
            Year = 2024,
            SongCount = count,
            IsExplicit = explicitAlbum,
            Upc = upc
        };
        Assert.Equal(accepted, CollectionMigrationService.SameAlbum(source, candidate));
    }

    [Fact]
    public void CleanAndExplicitTitleLabelsRemainDifferentWithoutRatingFlags()
    {
        var source = new ProviderLibraryResource("source", "Release (Clean)", ["Artist"], [], "2024", 12, null);
        Assert.False(CollectionMigrationService.SameAlbum(source, new() { Title = "Release (Explicit)", Artist = "Artist", Year = 2024, SongCount = 12 }));
    }

    [Fact]
    public void UnknownExplicitEditionOrDifferentYearNeedsReview()
    {
        var source = new ProviderLibraryResource("source", "Release", ["Artist"], [], "2024", 12, null);
        Assert.False(CollectionMigrationService.SameAlbum(source, new() { Title = "Release", Artist = "Artist", Year = 2023, SongCount = 12 }));
        Assert.False(CollectionMigrationService.SameAlbum(source, new() { Title = "Release", Artist = "Artist", Year = 2024, SongCount = 12, IsExplicit = true }));
    }
}
