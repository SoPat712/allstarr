using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task<LibraryRecordingLinkResult> LinkLibraryRecordingsAsync(ProviderActorContext actor,
        string correlationId, Guid? recordingId = null, Guid? libraryTrackId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        var user = actor.EffectiveUserId ?? throw new UnauthorizedAccessException("Library linking requires a user.");
        correlationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var focusTrack = libraryTrackId.HasValue ? await db.LibraryTracks.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == libraryTrackId.Value && item.OwnerUserId == user, cancellationToken) : null;
        if (libraryTrackId.HasValue && focusTrack == null) return new(0, 0, 0);
        if (recordingId.HasValue)
        {
            recordingId = (await FindSurvivorAsync(db, recordingId.Value, cancellationToken))?.Id;
            if (recordingId == null) return new(0, 0, 0);
        }
        var linkQuery = db.ProviderTrackIdentities.Where(item => item.ResourceKind == ProviderResourceKind.Track &&
            (!recordingId.HasValue || item.CanonicalRecordingId == recordingId.Value) &&
            (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                account.Id == item.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == user))));
        if (focusTrack != null)
        {
            var focusIsrc = NormalizeLibraryIsrc(focusTrack.Isrc);
            var focusMbid = LibraryMbid(focusTrack.MusicBrainzRecordingId);
            var titleWord = LibraryLinkTitle(focusTrack.Title, focusTrack.Artist).Split(' ').FirstOrDefault() ?? "";
            var escapedWord = JsonEncodedText.Encode(titleWord).ToString().ToLowerInvariant();
            linkQuery = linkQuery.Where(link => link.CanonicalRecordingId == focusTrack.CanonicalRecordingId ||
                db.RecordingIdentifiers.Any(identifier => identifier.RecordingId == link.CanonicalRecordingId &&
                    (focusIsrc != null && identifier.Kind == RecordingIdentifierKinds.Isrc && identifier.Value == focusIsrc ||
                     focusMbid != null && identifier.Kind == RecordingIdentifierKinds.MusicBrainz && identifier.Value == focusMbid)) ||
                titleWord != "" && db.ExternalMetadataSnapshots.Any(snapshot => snapshot.OwnerUserId == user &&
                    (snapshot.ResourceKind == "track" || snapshot.ResourceKind == "playback-metadata") &&
                    (snapshot.ProviderTrackIdentityId == link.Id || snapshot.ProviderTrackIdentityId == null &&
                        snapshot.ProviderId == link.ProviderId && snapshot.ExternalIdHash == link.ExternalIdHash) &&
                    (snapshot.PayloadJson.ToLower().Contains(titleWord) || snapshot.PayloadJson.ToLower().Contains(escapedWord))));
        }
        var links = await linkQuery.ToArrayAsync(cancellationToken);
        var automaticProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in links.Select(item => item.ProviderId).Distinct())
            if (await CanAutomaticallyLinkAsync(actor, provider, cancellationToken)) automaticProviders.Add(provider);
        var sourceIds = links.Where(item => automaticProviders.Contains(item.ProviderId) && IsLibraryLinkSource(item))
            .Select(item => item.CanonicalRecordingId).Distinct().ToArray();
        var recordings = await db.CanonicalRecordings.Include(item => item.Identifiers)
            .Where(item => item.MergedIntoId == null && sourceIds.Contains(item.Id))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArrayAsync(cancellationToken);
        var hashes = links.Select(item => item.ExternalIdHash).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user &&
            (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata") && hashes.Contains(item.ExternalIdHash))
            .ToArrayAsync(cancellationToken);
        var snapshotsBySource = snapshots.ToLookup(item => (item.ProviderId, item.ExternalIdHash));
        var linksByRecording = links.ToLookup(item => item.CanonicalRecordingId);
        var libraryQuery = db.LibraryTracks.Where(item => item.OwnerUserId == user);
        if (recordingId.HasValue || libraryTrackId.HasValue)
        {
            var storedEvidence = snapshots.Select(item => ReadLibraryLinkEvidence(item.PayloadJson)).OfType<LibraryLinkEvidence>().ToArray();
            var isrcs = recordings.SelectMany(item => item.Identifiers).Where(item => item.Kind == RecordingIdentifierKinds.Isrc)
                .Select(item => item.Value).Concat(storedEvidence.Select(item => item.Isrc).OfType<string>()).Distinct().ToArray();
            var mbids = recordings.SelectMany(item => item.Identifiers).Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)
                .Select(item => item.Value).Concat(storedEvidence.Select(item => item.Mbid).OfType<string>()).Distinct().ToArray();
            var titleWords = storedEvidence.Select(item => item.Title.Split(' ').FirstOrDefault() ?? "")
                .Where(item => item.Length > 0).Distinct().ToArray();
            libraryQuery = libraryQuery.Where(item => item.Id == libraryTrackId ||
                item.Isrc != null && isrcs.Contains(item.Isrc) ||
                item.MusicBrainzRecordingId != null && mbids.Contains(item.MusicBrainzRecordingId) ||
                titleWords.Any(word => item.Title.ToLower().Contains(word)));
        }
        var libraries = await libraryQuery.ToArrayAsync(cancellationToken);
        if (libraries.Length == 0) return new(recordings.Length, 0, 0);
        var libraryIds = libraries.Select(item => item.Id).ToArray();
        var overrides = await db.ManualTrackOverrides.AsNoTracking().Where(item => item.RevokedAt == null &&
            (hashes.Contains(item.SourceExternalIdHash) || item.LibraryTrackId != null && libraryIds.Contains(item.LibraryTrackId.Value)))
            .ToArrayAsync(cancellationToken);
        var byTitle = libraries.ToLookup(item => LibraryLinkTitle(item.Title, item.Artist));
        var byIsrc = libraries.Where(item => NormalizeLibraryIsrc(item.Isrc) != null).ToLookup(item => NormalizeLibraryIsrc(item.Isrc)!);
        var byMbid = libraries.Where(item => LibraryMbid(item.MusicBrainzRecordingId) != null).ToLookup(item => LibraryMbid(item.MusicBrainzRecordingId)!);
        var scanned = 0;
        var linked = 0;
        var conflicts = 0;
        foreach (var original in recordings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (original.MergedIntoId != null) continue;
            var recording = original;
            scanned++;
            var recordingLinks = linksByRecording[recording.Id].ToArray();
            if (recordingLinks.Any(item => item.Verification == ProviderIdentityVerification.Pinned ||
                    item.VerificationMethod is ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                        ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod) ||
                overrides.Any(choice => recordingLinks.Any(link => choice.SourceProviderId == link.ProviderId &&
                    choice.SourceExternalIdHash == link.ExternalIdHash))) continue;
            var evidence = recordingLinks.Where(item => automaticProviders.Contains(item.ProviderId) && IsLibraryLinkSource(item))
                .Select(link => snapshotsBySource[(link.ProviderId, link.ExternalIdHash)].Where(snapshot =>
                        (snapshot.ProviderTrackIdentityId == null || snapshot.ProviderTrackIdentityId == link.Id) &&
                        (link.Scope == ProviderIdentityScope.Catalog && snapshot.ProviderAccountId == null ||
                         link.Scope == ProviderIdentityScope.Account && snapshot.ProviderAccountId == link.ProviderAccountId))
                    .OrderByDescending(snapshot => snapshot.RetrievedAt).ThenByDescending(snapshot => snapshot.SnapshotVersion)
                    .ThenBy(snapshot => snapshot.ResourceKind == "track" ? 0 : 1)
                    .Select(snapshot => ReadLibraryLinkEvidence(snapshot.PayloadJson)).FirstOrDefault(item => item != null))
                .OfType<LibraryLinkEvidence>().ToArray();
            var codes = recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.Isrc).Select(item => item.Value)
                .Concat(evidence.Select(item => item.Isrc).OfType<string>()).ToHashSet(StringComparer.Ordinal);
            var mbids = recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz).Select(item => item.Value)
                .Concat(evidence.Select(item => item.Mbid).OfType<string>()).ToHashSet(StringComparer.Ordinal);
            var candidates = new Dictionary<Guid, (LibraryTrackRecord Track, int Strength)>();
            foreach (var code in codes)
                foreach (var track in byIsrc[code]) candidates[track.Id] = (track, 3);
            foreach (var mbid in mbids)
                foreach (var track in byMbid[mbid]) candidates.TryAdd(track.Id, (track, 2));
            foreach (var item in evidence)
                foreach (var track in byTitle[item.Title])
                    if (HasLibraryCreditEvidence(item, track) &&
                        (codes.Count == 0 || NormalizeLibraryIsrc(track.Isrc) is not { } localCode || codes.Contains(localCode)))
                        candidates.TryAdd(track.Id, (track, 1));
            if (candidates.Count == 0) continue;
            var strongest = candidates.Values.Max(item => item.Strength);
            var best = candidates.Values.Where(item => item.Strength == strongest)
                .Where(item => !overrides.Any(choice => choice.LibraryTrackId == item.Track.Id)).ToArray();
            if (best.Length == 0) continue;
            // Multiple copies of one established recording are safe; competing recordings remain separate.
            var groups = best.GroupBy(item => LibraryLinkKey(item.Track)).ToArray();
            if (groups.Length != 1) { conflicts++; continue; }
            var selected = groups[0].Select(item => item.Track).ToArray();
            if (libraryTrackId.HasValue && !selected.Any(item => item.Id == libraryTrackId.Value)) continue;
            var localMbid = selected.Select(item => LibraryMbid(item.MusicBrainzRecordingId)).OfType<string>().Distinct().ToArray();
            if (localMbid.Concat(mbids).Distinct().Count() > 1) { conflicts++; continue; }
            var local = selected[0];
            CanonicalRecordingRecord? target;
            try
            {
                target = local.CanonicalRecordingId is { } localId
                    ? await FindSurvivorAsync(db, localId, cancellationToken)
                    : await FindCanonicalByExactSignalsAsync(db, NormalizeLibraryIsrc(local.Isrc), LibraryMbid(local.MusicBrainzRecordingId), cancellationToken);
                var exact = await FindCanonicalByExactSignalsAsync(db, NormalizeLibraryIsrc(local.Isrc),
                    LibraryMbid(local.MusicBrainzRecordingId), cancellationToken);
                if (exact != null && target != null && exact.Id != target.Id && exact.Id != recording.Id)
                { conflicts++; continue; }
            }
            catch (InvalidOperationException) { conflicts++; continue; }
            if (target != null && target.Id != recording.Id)
            {
                var targetLinks = await db.ProviderTrackIdentities.Where(item => item.CanonicalRecordingId == target.Id).ToArrayAsync(cancellationToken);
                if (await HasAvailabilityAuthorityConflictAsync(db, targetLinks, user, cancellationToken) ||
                    overrides.Any(choice => targetLinks.Any(link => choice.SourceProviderId == link.ProviderId &&
                        choice.SourceExternalIdHash == link.ExternalIdHash)) ||
                    !await HasAutomaticProviderIdentityAsync(db, target.Id, actor, cancellationToken) ||
                    target.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz).Select(item => item.Value)
                        .Concat(mbids).Concat(localMbid).Distinct().Count() > 1)
                { conflicts++; continue; }
            }
            var now = _clock.UtcNow;
            var previousRecordingId = recording.Id;
            var previousLocalRecordingId = target?.Id;
            var changedTracks = selected.Where(item => item.CanonicalRecordingId != recording.Id).Select(item => item.Id).ToHashSet();
            if (target != null && target.Id != recording.Id)
            {
                try { recording = await MergeRecordsAsync(db, recording, target, actor, correlationId, now, cancellationToken); }
                catch (RecordingMergeConflictException) { conflicts++; continue; }
            }
            var changed = changedTracks.Count > 0;
            linked += changedTracks.Count;
            foreach (var track in selected)
            {
                if (track.CanonicalRecordingId == recording.Id) continue;
                track.CanonicalRecordingId = recording.Id;
                track.UpdatedAt = now;
                track.Revision++;
            }
            var enriched = AttachIdentifier(db, recording, RecordingIdentifierKinds.MusicBrainz,
                localMbid.FirstOrDefault(), "library-link", now);
            foreach (var code in selected.Select(item => NormalizeLibraryIsrc(item.Isrc)).OfType<string>().Distinct())
                enriched |= AttachIdentifier(db, recording, RecordingIdentifierKinds.Isrc, code, "library-link", now);
            if (enriched)
            {
                recording.IsProvisional = Identifier(recording, RecordingIdentifierKinds.MusicBrainz) == null;
                recording.Revision++;
                recording.UpdatedAt = now;
            }
            if (changed || previousRecordingId != recording.Id || previousLocalRecordingId != null && previousLocalRecordingId != recording.Id)
                AddAudit(db, actor, correlationId, "canonical-recording.library-link", "linked", new
                {
                    recordingId = recording.Id,
                    sourceRecordingId = previousRecordingId,
                    localRecordingId = previousLocalRecordingId,
                    libraryTrackIds = selected.Select(item => item.Id),
                    evidence = strongest switch { 3 => "isrc", 2 => "musicbrainz", _ => "title-artist-duration" }
                });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(scanned, linked, conflicts);
    }

    private static bool IsLibraryLinkSource(ProviderTrackIdentityRecord link) =>
        link.Verification == ProviderIdentityVerification.Verified && CatalogIdentityPolicy.IsPlaybackEligible(link) &&
        link.VerificationMethod != "automatic-suggestion" && link.VerificationMethod != "source-snapshot-hash";

    private static string LibraryLinkKey(LibraryTrackRecord track) => track.CanonicalRecordingId?.ToString("D") ??
        (LibraryMbid(track.MusicBrainzRecordingId) is { } mbid ? "mbid:" + mbid :
         NormalizeLibraryIsrc(track.Isrc) is { } isrc ? "isrc:" + isrc : "track:" + track.Id.ToString("D"));

    private static string? NormalizeLibraryIsrc(string? value)
    {
        var cleaned = value?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return cleaned != null && IsrcPattern.IsMatch(cleaned) ? cleaned : null;
    }

    private static string? LibraryMbid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : null;

    private static string LinkText(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC)
        .ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"\s*[\(\[]\s*prod(?:uced)?\.?(?:\s+by)?\s+[^\)\]]+[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductionCreditRegex();

    private static string LibraryLinkTitle(string title, string? artists) => FuzzyMatcher.NormalizeTitleForMatching(
        ProductionCreditRegex().Replace(title, match => FuzzyMatcher.RecordingVersionTags(match.Value).Count == 0 ? " " : match.Value), artists);

    private sealed record LibraryLinkEvidence(string Title, HashSet<string>[] Credits, long? Duration, string? Isrc, string? Mbid);

    private static bool HasLibraryCreditEvidence(LibraryLinkEvidence evidence, LibraryTrackRecord track)
    {
        if (evidence.Title.Length == 0 || evidence.Duration is not > 0 || track.DurationMilliseconds is not > 0 ||
            Math.Abs(evidence.Duration.Value - track.DurationMilliseconds.Value) > 2000 || evidence.Credits.Length == 0) return false;
        return new[] { track.Artist, track.AlbumArtist }.OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Any(credit =>
        {
            var names = FuzzyMatcher.SplitCredits(credit).Select(LinkText).Distinct().ToArray();
            if (names.Length != evidence.Credits.Length || names.Any(name => name is "VARIOUS ARTISTS" or "VARIOUS")) return false;
            var matches = names.Select(name => evidence.Credits.Select((aliases, index) => (aliases, index))
                .Where(item => item.aliases.Contains(name)).Select(item => item.index).ToArray()).ToArray();
            return matches.All(item => item.Length == 1) && matches.Select(item => item[0]).Distinct().Count() == names.Length;
        });
    }

    private static LibraryLinkEvidence? ReadLibraryLinkEvidence(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            static JsonElement Field(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().FirstOrDefault(item => names.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).Value : default;
            static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var title = Text(Field(root, "Title", "Name"));
            if (string.IsNullOrWhiteSpace(title)) return null;
            var credits = new List<HashSet<string>>();
            void Add(JsonElement value, HashSet<string> names)
            {
                var name = Text(value) ?? Text(Field(value, "Name"));
                if (!string.IsNullOrWhiteSpace(name)) names.Add(LinkText(name));
            }
            void Aliases(JsonElement value, HashSet<string> names)
            {
                if (value.ValueKind == JsonValueKind.Array)
                    foreach (var alias in value.EnumerateArray()) Add(alias, names);
            }
            if (Field(root, "Artists", "ArtistCredit", "artist-credit") is { ValueKind: JsonValueKind.Array } artists)
                foreach (var artist in artists.EnumerateArray())
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    Add(artist, names);
                    Aliases(Field(artist, "Aliases"), names);
                    var creditedArtist = Field(artist, "Artist");
                    Add(creditedArtist, names);
                    Aliases(Field(creditedArtist, "Aliases"), names);
                    if (names.Count > 0) credits.Add(names);
                }
            if (credits.Count == 0 && Text(Field(root, "Artist")) is { } artistName)
                credits.AddRange(FuzzyMatcher.SplitCredits(artistName).Select(name => new HashSet<string>(StringComparer.Ordinal) { LinkText(name) }));
            if (credits.Count == 1) Aliases(Field(root, "ArtistAliases"), credits[0]);
            long? Number(params string[] fields) => Field(root, fields) is { ValueKind: JsonValueKind.Number } value &&
                value.TryGetInt64(out var number) ? number : null;
            var duration = Number("DurationMilliseconds", "DurationMs", "Length") ?? Number("Duration") * 1000;
            if (duration == null && TimeSpan.TryParse(Text(Field(root, "Duration")), CultureInfo.InvariantCulture, out var precise))
                duration = (long)precise.TotalMilliseconds;
            if (duration is <= 0 or > 86400000) duration = null;
            return new(LibraryLinkTitle(title, string.Join(", ", credits.SelectMany(item => item))), credits.ToArray(), duration, NormalizeLibraryIsrc(Text(Field(root, "Isrc"))),
                LibraryMbid(Text(Field(root, "MusicBrainzRecordingId"))));
        }
        catch (JsonException) { return null; }
    }
}
