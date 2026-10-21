namespace allstarr.Services.Jellyfin;

internal static class JellyfinLyricStreams
{
    public static void AddTo(Dictionary<string, object?> item)
    {
        if (item.GetValueOrDefault("HasLyrics") is not true ||
            item.GetValueOrDefault("MediaSources") is not Dictionary<string, object?>[] sources) return;

        foreach (var source in sources)
        {
            if (source.GetValueOrDefault("MediaStreams") is not Dictionary<string, object?>[] streams ||
                streams.Any(stream => Equals(stream.GetValueOrDefault("Type"), "Lyric"))) continue;

            // Like HasLyrics, this invites a lookup; an unavailable source still returns 404.
            source["MediaStreams"] = streams.Append(new Dictionary<string, object?>
            {
                ["Type"] = "Lyric",
                ["Index"] = streams.Select(stream => stream.GetValueOrDefault("Index") is int index ? index : -1)
                    .DefaultIfEmpty(-1).Max() + 1,
                ["IsExternal"] = true,
                ["IsInterlaced"] = false,
                ["IsDefault"] = false,
                ["IsForced"] = false,
                ["IsHearingImpaired"] = false,
                ["IsOriginal"] = false,
                ["IsTextSubtitleStream"] = false,
                ["SupportsExternalStream"] = false
            }).ToArray();
        }
    }
}
