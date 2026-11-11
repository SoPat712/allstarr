using System.Text.Json.Serialization;

namespace allstarr.Core.Music;

public sealed record MixCoverInput(string Id, int Generation, string Kind, string Tag = "", string Artists = "",
    string Seed = "", string Source = "", string SourcePlaylistId = "", string Name = "", int Count = 0,
    IReadOnlyList<string>? Art = null, string? ServiceArtwork = null, Guid? ProviderAccountId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? FallbackArt = null)
{
    internal static MixCoverInput From(PersonalMix mix)
    {
        if (mix.Kind == "external" && (mix.Source is "lastfm" or "listenbrainz") && mix.ArtworkUrl == null &&
            (mix.SongCount ?? mix.Songs.Count) == 0)
            return new(mix.Id.ToString(), mix.Generation, mix.Kind, Source: mix.Source ?? "", SourcePlaylistId: mix.SourcePlaylistId ?? "", Name: mix.Name);
        var parts = mix.Name.Split(" · ", 3);
        var daily = mix.Kind.StartsWith("daily-", StringComparison.Ordinal);
        var art = mix.ArtworkUrl is { } artwork ? mix.Kind == "external" ? [] : new[] { artwork }
            : MixCoverArt.Pick(mix.Songs.Select(item => item.Song)).Select(song => MixCoverArt.Reference(song)!).ToArray();
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mix.ArtworkUrl == null)
            foreach (var item in mix.Songs)
                if (MixCoverArt.Reference(item.Song) is { } reference)
                    candidates.TryAdd(reference, MixCoverArt.AlbumKey(item.Song, reference));
        return new(mix.Id.ToString(), mix.Generation, mix.Kind,
            mix.CoverTag ?? (daily && parts.Length > 1 ? parts[1] : daily && mix.Name.EndsWith(" Blend", StringComparison.Ordinal) ? mix.Name[..^6] : ""),
            !daily ? "" : parts.Length > 2 ? parts[2] : string.Join(", ", mix.Songs.GroupBy(item => item.Song.Artist)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(3).Select(group => group.Key)),
            mix.SeedName ?? mix.Name, Source: mix.Source ?? "", SourcePlaylistId: mix.SourcePlaylistId ?? "", Name: mix.Name, Count: mix.SongCount ?? mix.Songs.Count,
            Art: art, ServiceArtwork: mix.Kind == "external" ? mix.ArtworkUrl : null, ProviderAccountId: mix.ProviderAccountId,
            FallbackArt: candidates.Count > art.Distinct(StringComparer.Ordinal).Count() ? candidates : null);
    }
}
