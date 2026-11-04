using System.Globalization;

namespace allstarr.Core.Music;

internal static class PersonalMixDescriptions
{
    internal static string Tagline(string kind, string? style = null)
    {
        if (PersonalMixMoods.Kinds.Contains(kind)) return $"Music for {kind}, updated Monday";
        if (kind.StartsWith("daily-", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(style)) return "Familiar favorites and new songs in your top styles";
            var label = PersonalMixRules.Name(kind, string.Join(" ", style.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            return label[..^" Blend".Length] + " you play most";
        }
        return kind switch
        {
            "on-repeat" => "What you're playing most right now",
            "rewind" => "Songs you loved and haven't played lately",
            "release-radar" => "New from artists you play",
            "discover-weekly" => "New to you, based on your taste",
            _ => throw new ArgumentException("Unknown mix kind.")
        };
    }

    internal static string FromSelection(string kind, string? style, IReadOnlyList<MixCandidate> selected, string? historicalWindow)
    {
        if (selected.Count > 0)
        {
            if (kind == "on-repeat" && historicalWindow != null)
                return "Your most played, including imported " + historicalWindow;
            if (kind == "on-repeat" && historicalWindow == null)
                return $"Your most played, last {(selected.All(row => row.RecentPlays > 0) ? MixRecipes.RecentDays : MixRecipes.RotationDays)} days";
            if (kind == "rewind" && selected.All(row => row.LastPlayed != null))
                return "Not played since " + selected.Max(row => row.LastPlayed)!.Value.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            if (kind == "release-radar") return Releases(selected.Select(row => row.Song.Artist));
        }
        return Tagline(kind, style);
    }

    internal static string OnRead(PersonalMix mix, string? style)
    {
        if (!string.IsNullOrWhiteSpace(mix.Description) &&
            !mix.Description.StartsWith("Made for you", StringComparison.OrdinalIgnoreCase) &&
            !mix.Description.StartsWith("Updated Monday", StringComparison.OrdinalIgnoreCase) &&
            !mix.Description.Contains("Daily Mix", StringComparison.OrdinalIgnoreCase) &&
            !mix.Description.Contains(mix.Name, StringComparison.OrdinalIgnoreCase)) return mix.Description;
        return mix.Kind == "release-radar" ? Releases(mix.Songs.Select(item => item.Song.Artist)) : Tagline(mix.Kind, style);
    }

    private static string Releases(IEnumerable<string> artists)
    {
        var count = artists.Where(artist => !string.IsNullOrWhiteSpace(artist)).Select(artist => artist.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return count == 0 ? Tagline("release-radar") : $"New from {count} {(count == 1 ? "artist" : "artists")} you follow or play";
    }
}
