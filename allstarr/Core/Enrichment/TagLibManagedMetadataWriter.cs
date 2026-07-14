using TagLib;

namespace allstarr.Core.Enrichment;

/// <summary>Writes bounded common tags only after ManagedMetadataPlanApplicator proves managed-file ownership.</summary>
public sealed class TagLibManagedMetadataWriter : IManagedMetadataWriter
{
    public Task WriteAsync(ManagedMetadataArtifact artifact, IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = TagLib.File.Create(artifact.Path);
        if (tags.TryGetValue("title", out var title)) file.Tag.Title = title;
        if (tags.TryGetValue("artist", out var artist)) file.Tag.Performers = [artist];
        if (tags.TryGetValue("album", out var album)) file.Tag.Album = album;
        if (tags.TryGetValue("albumArtist", out var albumArtist)) file.Tag.AlbumArtists = [albumArtist];
        if (tags.TryGetValue("genre", out var genre)) file.Tag.Genres = genre.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.TryGetValue("year", out var year) && uint.TryParse(year, out var parsedYear)) file.Tag.Year = parsedYear;
        if (tags.TryGetValue("track", out var track) && uint.TryParse(track, out var parsedTrack)) file.Tag.Track = parsedTrack;
        cancellationToken.ThrowIfCancellationRequested();
        file.Save();
        return Task.CompletedTask;
    }
}
