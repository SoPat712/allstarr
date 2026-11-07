using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

internal static class SelfLoveMatchFixture
{
    internal const string Title = "Self Love (Spider-Man: Across the Spider-Verse) (Metro Boomin & Coi Leray)";
    internal const string Artist = "Metro Boomin, Coi Leray";
    internal const string Album = "METRO BOOMIN PRESENTS SPIDER-MAN: ACROSS THE SPIDER-VERSE (SOUNDTRACK FROM AND INSPIRED BY THE MOTION PICTURE)";
    internal const string LibraryAlbum = "METRO BOOMIN PRESENTS SPIDER‐MAN: ACROSS THE SPIDER‐VERSE: SOUNDTRACK FROM AND INSPIRED BY THE MOTION PICTURE";
    internal const string BackendItemId = "f415372467ec28f223dc87947c14e6e1";

    internal static ExternalTrackMatchSnapshot Source() => new(
        "self-love", "spotify", "source", Title, Artist, Album, null, 189466, null, null, false);

    internal static string Payload => JsonSerializer.Serialize(new
    {
        Title,
        Artist,
        Album,
        DurationMilliseconds = 189466,
        IsExplicit = false
    });

    internal static LibraryTrackRecord Local(Guid owner, string backend, string library = "music") => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = backend,
        BackendLibraryId = library,
        BackendItemId = BackendItemId,
        FilePath = "/music/Self Love.flac",
        Title = "Self Love",
        Artist = Artist,
        Album = LibraryAlbum,
        DurationMilliseconds = 189466,
        ProviderIdsJson = """{"musicbrainzalbum":"release"}"""
    };

    internal static LocalTrackMatchCandidate External(string backend) => new(
        Guid.NewGuid(), null, backend, null, "external", null,
        "Self Love (Spider-Man: Across the Spider-Verse)", "Metro Boomin", Album, null,
        189000, "USUM72308561", null, false,
        new Dictionary<string, string> { ["apple-musickit"] = "1700522647", ["deezer"] = "fallback" }, IsLocal: false);
}
