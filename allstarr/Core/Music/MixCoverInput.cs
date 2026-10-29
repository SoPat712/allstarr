namespace allstarr.Core.Music;

public sealed record MixCoverInput(string Id, int Generation, string Kind, string Tag = "", string Artists = "",
    string Seed = "", string Source = "", string SourcePlaylistId = "", string Name = "", int Count = 0)
{
    internal static MixCoverInput From(PersonalMix mix)
    {
        var parts = mix.Name.Split(" · ", 3);
        var daily = mix.Kind.StartsWith("daily-", StringComparison.Ordinal);
        return new(mix.Id.ToString(), mix.Generation, mix.Kind,
            mix.CoverTag ?? (daily && parts.Length > 1 ? parts[1] : ""),
            !daily ? "" : parts.Length > 2 ? parts[2] : string.Join(", ", mix.Songs.GroupBy(item => item.Song.Artist)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(3).Select(group => group.Key)),
            mix.SeedName ?? mix.Name, Name: mix.Name, Count: mix.Songs.Count);
    }
}
