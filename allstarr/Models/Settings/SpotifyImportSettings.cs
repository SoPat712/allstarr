namespace allstarr.Models.Settings;

using System.Text.Json;

public enum LocalTracksPosition
{
    First,
    Last
}

public sealed class SpotifyPlaylistConfig
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string JellyfinId { get; set; } = string.Empty;
    public LocalTracksPosition LocalTracksPosition { get; set; } = LocalTracksPosition.First;
    public string SyncSchedule { get; set; } = "0 8 * * *";
    public string? UserId { get; set; }
}

public static class SpotifyPlaylistConfigParser
{
    public static string Serialize(IEnumerable<SpotifyPlaylistConfig> playlists) =>
        JsonSerializer.Serialize(playlists.Select(playlist =>
        {
            var values = new List<string>
            {
                playlist.Name ?? string.Empty,
                playlist.Id ?? string.Empty,
                playlist.JellyfinId ?? string.Empty,
                playlist.LocalTracksPosition.ToString().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(playlist.SyncSchedule) ? "0 8 * * *" : playlist.SyncSchedule.Trim()
            };
            if (!string.IsNullOrWhiteSpace(playlist.UserId)) values.Add(playlist.UserId.Trim());
            return values.ToArray();
        }));

    public static List<SpotifyPlaylistConfig> Parse(string json)
    {
        string[][] entries;
        try
        {
            entries = JsonSerializer.Deserialize<string[][]>(json) ?? [];
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "SpotifyImport:Playlists must be a JSON array of playlist arrays.");
        }

        var playlists = new List<SpotifyPlaylistConfig>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Length is < 2 or > 6 ||
                string.IsNullOrWhiteSpace(entry[0]) ||
                string.IsNullOrWhiteSpace(entry[1]))
            {
                throw new InvalidOperationException(
                    "Each SpotifyImport:Playlists entry requires a name and provider playlist ID.");
            }

            var jellyfinId = string.Empty;
            var position = LocalTracksPosition.First;
            var schedule = "0 8 * * *";
            string? userId = null;
            if (entry.Length >= 3)
            {
                var third = entry[2].Trim();
                var thirdIsPosition = third.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                                      third.Equals("last", StringComparison.OrdinalIgnoreCase);
                if (thirdIsPosition)
                {
                    position = third.Equals("last", StringComparison.OrdinalIgnoreCase)
                        ? LocalTracksPosition.Last
                        : LocalTracksPosition.First;
                    schedule = entry.Length >= 4 && !string.IsNullOrWhiteSpace(entry[3]) ? entry[3].Trim() : schedule;
                    userId = entry.Length >= 5 && !string.IsNullOrWhiteSpace(entry[4]) ? entry[4].Trim() : null;
                }
                else
                {
                    jellyfinId = third;
                    position = entry.Length >= 4 && entry[3].Trim().Equals("last", StringComparison.OrdinalIgnoreCase)
                        ? LocalTracksPosition.Last
                        : LocalTracksPosition.First;
                    schedule = entry.Length >= 5 && !string.IsNullOrWhiteSpace(entry[4]) ? entry[4].Trim() : schedule;
                    userId = entry.Length >= 6 && !string.IsNullOrWhiteSpace(entry[5]) ? entry[5].Trim() : null;
                }
            }

            playlists.Add(new SpotifyPlaylistConfig
            {
                Name = entry[0].Trim(),
                Id = entry[1].Trim(),
                JellyfinId = jellyfinId,
                LocalTracksPosition = position,
                SyncSchedule = schedule,
                UserId = userId
            });
        }

        return playlists;
    }
}

public sealed class SpotifyImportSettings
{
    public bool Enabled { get; set; }
    public int MatchingIntervalHours { get; set; } = 24;
    public List<SpotifyPlaylistConfig> Playlists { get; set; } = new();
    public SpotifyPlaylistConfig? GetPlaylistById(string playlistId) =>
        Playlists.FirstOrDefault(p => p.Id.Equals(playlistId, StringComparison.OrdinalIgnoreCase));
    public SpotifyPlaylistConfig? GetPlaylistByJellyfinId(string jellyfinPlaylistId) =>
        Playlists.FirstOrDefault(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
    public SpotifyPlaylistConfig? GetPlaylistByName(string name) =>
        Playlists.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool IsSpotifyPlaylist(string jellyfinPlaylistId) =>
        Playlists.Any(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
}
