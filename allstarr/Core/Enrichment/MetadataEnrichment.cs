using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Enrichment;

public sealed record MetadataField(string? Value, bool UserEdited = false);

public sealed record LocalMetadataSnapshot(
    MetadataField Title,
    MetadataField Artist,
    MetadataField? Album = null,
    MetadataField? AlbumArtist = null,
    MetadataField? Genre = null,
    MetadataField? Year = null,
    MetadataField? Track = null);

public sealed record MusicBrainzEnrichmentSnapshot(
    string? RecordingId,
    string? ReleaseId,
    string? ReleaseGroupId,
    string? ArtistId,
    string? Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    IReadOnlyList<string>? Genres,
    int? Year,
    int? Track);

public sealed record ProviderMetadataSnapshot(
    string ProviderId,
    string Revision,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record MetadataMergeDecision(string Field, string Source, string Reason);

public sealed record MetadataEnrichmentPlan(
    int Version,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> PathValues,
    IReadOnlyList<MetadataMergeDecision> Decisions,
    IReadOnlyList<string> SourceRevisions,
    bool ManagedArtifactsOnly = true);

public interface IMetadataEnrichmentPlanner
{
    MetadataEnrichmentPlan CreatePlan(
        LocalMetadataSnapshot local,
        MusicBrainzEnrichmentSnapshot? musicBrainz,
        IReadOnlyList<ProviderMetadataSnapshot>? providers = null);
}

/// <summary>
/// Creates a deterministic, reviewable tag and path plan. It never opens or writes a media file.
/// Local user-edited values win, MusicBrainz fills identity/credits/release data, then providers fill gaps.
/// </summary>
public sealed class MetadataEnrichmentPlanner : IMetadataEnrichmentPlanner
{
    public MetadataEnrichmentPlan CreatePlan(LocalMetadataSnapshot local,
        MusicBrainzEnrichmentSnapshot? musicBrainz, IReadOnlyList<ProviderMetadataSnapshot>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        providers ??= [];
        if (providers.Count > 32) throw new ArgumentException("At most 32 provider snapshots may contribute to one enrichment plan.", nameof(providers));
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            var id = provider.ProviderId.Trim().ToLowerInvariant();
            if (id.Length is < 1 or > 100 || !id.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.'))
                throw new ArgumentException("Provider IDs in enrichment snapshots are invalid.", nameof(providers));
            if (!providerIds.Add(id)) throw new ArgumentException("Provider snapshots must have unique provider IDs.", nameof(providers));
            if (string.IsNullOrWhiteSpace(provider.Revision) || provider.Revision.Trim().Length > 200 || provider.Fields.Count > 100)
                throw new ArgumentException("Provider snapshot revisions or fields are invalid.", nameof(providers));
        }
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var decisions = new List<MetadataMergeDecision>();

        Merge("title", local.Title, musicBrainz?.Title, providers, tags, decisions);
        Merge("artist", local.Artist, musicBrainz?.Artist, providers, tags, decisions);
        Merge("album", local.Album, musicBrainz?.Album, providers, tags, decisions);
        Merge("albumArtist", local.AlbumArtist, musicBrainz?.AlbumArtist, providers, tags, decisions);
        Merge("genre", local.Genre, musicBrainz?.Genres is { Count: > 0 } genres ? string.Join("; ", genres.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) : null, providers, tags, decisions);
        Merge("year", local.Year, musicBrainz?.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture), providers, tags, decisions);
        Merge("track", local.Track, musicBrainz?.Track?.ToString(System.Globalization.CultureInfo.InvariantCulture), providers, tags, decisions);
        AddMbid("musicbrainz_recordingid", musicBrainz?.RecordingId, tags, decisions);
        AddMbid("musicbrainz_releaseid", musicBrainz?.ReleaseId, tags, decisions);
        AddMbid("musicbrainz_releasegroupid", musicBrainz?.ReleaseGroupId, tags, decisions);
        AddMbid("musicbrainz_artistid", musicBrainz?.ArtistId, tags, decisions);

        var path = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "title", "artist", "album", "albumArtist", "genre", "year", "track" })
            if (tags.TryGetValue(key, out var value)) path[key] = value;
        var revisions = providers.Select(value => $"{value.ProviderId.Trim().ToLowerInvariant()}:{value.Revision.Trim()}")
            .Order(StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.Serialize(new { version = 1, tags, path, decisions, revisions });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(1, fingerprint, tags, path, decisions, revisions);
    }

    private static void Merge(string field, MetadataField? local, string? musicBrainz,
        IReadOnlyList<ProviderMetadataSnapshot> providers, IDictionary<string, string> tags,
        ICollection<MetadataMergeDecision> decisions)
    {
        if (!string.IsNullOrWhiteSpace(local?.Value))
        {
            tags[field] = local.Value.Trim();
            decisions.Add(new(field, "local", local.UserEdited ? "local_user_edit_preserved" : "local_value_preserved"));
            return;
        }
        if (!string.IsNullOrWhiteSpace(musicBrainz))
        {
            tags[field] = musicBrainz.Trim();
            decisions.Add(new(field, "musicbrainz", "filled_missing_local_value"));
            return;
        }
        foreach (var provider in providers)
        {
            if (provider.Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                tags[field] = value.Trim();
                decisions.Add(new(field, provider.ProviderId.Trim().ToLowerInvariant(), "filled_missing_enrichment_value"));
                return;
            }
        }
    }

    private static void AddMbid(string field, string? value, IDictionary<string, string> tags,
        ICollection<MetadataMergeDecision> decisions)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Guid.TryParse(value, out var id)) throw new ArgumentException($"{field} must be a MusicBrainz UUID.");
        tags[field] = id.ToString("D");
        decisions.Add(new(field, "musicbrainz", "canonical_identity"));
    }
}

public sealed record ManagedMetadataArtifact(string Path, string ContentSha256, bool IsAllstarrManaged, bool IsSourceLibraryFile);
public interface IManagedMetadataWriter
{
    Task WriteAsync(ManagedMetadataArtifact artifact, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken);
}

public sealed class ManagedMetadataPlanApplicator(IManagedMetadataWriter writer)
{
    public async Task ApplyAsync(ManagedMetadataArtifact artifact, MetadataEnrichmentPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ManagedArtifactsOnly || !artifact.IsAllstarrManaged || artifact.IsSourceLibraryFile)
            throw new InvalidOperationException("Metadata plans may be applied only to an Allstarr-managed artifact, never a source-library file.");
        if (string.IsNullOrWhiteSpace(artifact.Path) || artifact.Path.IndexOf('\0') >= 0 ||
            artifact.ContentSha256.Length != 64 || !artifact.ContentSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The managed artifact reference is invalid.", nameof(artifact));
        await writer.WriteAsync(artifact, plan.Tags, cancellationToken);
    }
}
