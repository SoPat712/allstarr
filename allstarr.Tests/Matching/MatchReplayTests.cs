using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Services.Common;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class MatchReplayTests(ITestOutputHelper output)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ReplayStoredDecisionsWithoutProviders()
    {
        var snapshot = Environment.GetEnvironmentVariable("ALLSTARR_MATCH_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            output.WriteLine("The offline replay is opt-in and was not run; set ALLSTARR_MATCH_SNAPSHOT to run it.");
            return;
        }
        var path = Path.GetFullPath(snapshot);
        var directory = Path.GetDirectoryName(path)!;
        var measurement = Path.Combine(directory, "measurement.json");
        Assert.NotEqual(path, measurement);
        var configuredSource = Environment.GetEnvironmentVariable("ALLSTARR_MATCH_SOURCE");
        if (!string.IsNullOrWhiteSpace(configuredSource)) Assert.NotEqual(Path.GetFullPath(configuredSource), measurement);
        var created = false;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(directory);
        }
        RequirePrivate(directory, directory: true);
        try
        {
            if (!File.Exists(path))
            {
                var live = Environment.GetEnvironmentVariable("ALLSTARR_MATCH_SOURCE");
                Assert.False(string.IsNullOrWhiteSpace(live));
                live = Path.GetFullPath(live!);
                if (Directory.Exists(live))
                {
                    var databases = Directory.GetFiles(live, "allstarr.db", SearchOption.AllDirectories)
                        .Where(item => !item.Contains("backup", StringComparison.OrdinalIgnoreCase)).ToArray();
                    Assert.Single(databases);
                    live = databases[0];
                }
                Assert.NotEqual(live, path);
                Assert.NotEqual(live, measurement);
                using var source = Open(live, SqliteOpenMode.ReadOnly);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (new FileStream(path, options)) { }
                created = true;
                using (var destination = Open(path, SqliteOpenMode.ReadWrite)) source.BackupDatabase(destination);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
            }
            RequirePrivate(path);
            foreach (var suffix in Sidecars) if (File.Exists(path + suffix)) RequirePrivate(path + suffix);
            using var db = Open(path, SqliteOpenMode.ReadOnly);
            using var check = db.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            Assert.Equal("ok", check.ExecuteScalar());
            var report = Replay(db);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            Assert.Null(new FileInfo(measurement).LinkTarget);
            var writeOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(measurement, writeOptions))
            using (var writer = new StreamWriter(file)) writer.Write(json);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(measurement, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            output.WriteLine("MATCH_REPLAY_BEGIN\n" + json + "\nMATCH_REPLAY_END");
        }
        finally
        {
            if (created)
            {
                File.Delete(path);
                foreach (var suffix in Sidecars) File.Delete(path + suffix);
                Assert.False(File.Exists(path));
                foreach (var suffix in Sidecars) Assert.False(File.Exists(path + suffix));
            }
        }
    }

    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    private static void RequirePrivate(string path, bool directory = false)
    {
        if (!OperatingSystem.IsLinux()) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode foreign = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal((UnixFileMode)0, mode & foreign);
        Assert.True((mode & UnixFileMode.UserRead) != 0);
        if (directory) Assert.True((mode & (UnixFileMode.UserWrite | UnixFileMode.UserExecute)) ==
                                   (UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private static object Replay(SqliteConnection db)
    {
        var watch = Stopwatch.StartNew();
        var tables = new Dictionary<string, long>();
        foreach (var row in Query(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
        {
            var table = (string)row[0]!;
            if (!new[] { "track_matches", "external_metadata_snapshots", "playlist_source_entries", "favorite_states",
                "favorite_actions", "listening_events", "listening_history_imports", "provider_track_identities",
                "recording_kept_routes", "manual_track_overrides", "library_tracks" }.Contains(table, StringComparer.Ordinal)) continue;
            tables[table] = Convert.ToInt64(Query(db, $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")[0][0]);
        }
        var settings = Query(db, "SELECT Key, ValueJson FROM runtime_settings WHERE OwnerUserId IS NULL AND Key IN ('Providers:StreamingOrder','Matching:LocalPreferencePercent')");
        var configured = settings.FirstOrDefault(row => (string)row[0]! == "Providers:StreamingOrder");
        var order = configured == null ? new[] { "apple-download", "deezer", "qobuz" } : ReadOrder((string)configured[1]!);
        var preference = settings.FirstOrDefault(row => (string)row[0]! == "Matching:LocalPreferencePercent");
        var window = preference == null ? .07 : JsonSerializer.Deserialize<double>((string)preference[1]!) / 100;
        var policy = new TrackMatchPolicy { LocalPriorityWindow = window };
        var current = new ReplayBaselineEngine(policy);
        var candidate = new TrackMatchDecisionEngine(policy);
        var counts = new Dictionary<string, int>();
        var affectedSongs = new Dictionary<string, HashSet<string>>();
        var groups = new Dictionary<string, List<object>>();
        var changes = new List<object>();
        var examples = new Dictionary<string, List<object>>();
        var metadata = Query(db, "SELECT ProviderId, ExternalIdHash, PayloadJson, RetrievedAt FROM external_metadata_snapshots WHERE ResourceKind IN ('track','playback-metadata')")
            .Select(row =>
            {
                using var document = JsonDocument.Parse((string)row[2]!);
                return (Provider: NormalizeProvider((string)row[0]!), Hash: (string)row[1]!, Root: document.RootElement.Clone(), Retrieved: new DateTimeOffset(Convert.ToInt64(row[3]), TimeSpan.Zero));
            }).GroupBy(item => (item.Provider, item.Hash)).ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Retrieved).ToArray());
        var library = Query(db, "SELECT Id, CanonicalRecordingId, AlbumArtist, MusicBrainzRecordingId, UpdatedAt, Title, Artist, Album, DurationMilliseconds FROM library_tracks")
            .ToDictionary(row => Guid.Parse((string)row[0]!));
        var decisions = Query(db, """
            SELECT m.State, m.CandidateResultsJson, m.MatcherVersion,
                   s.PayloadJson, s.ProviderId, m.ReasonsJson,
                   CASE WHEN m.DecisionVersion=(SELECT max(n.DecisionVersion) FROM track_matches n WHERE n.ExternalSnapshotId=m.ExternalSnapshotId) THEN 1 ELSE 0 END,
                   coalesce(p.ExternalId, s.ExternalIdHash), m.DecidedAt
            FROM track_matches m LEFT JOIN external_metadata_snapshots s ON s.Id=m.ExternalSnapshotId
            LEFT JOIN provider_track_identities p ON p.Id=s.ProviderTrackIdentityId
            ORDER BY m.ExternalSnapshotId, m.DecisionVersion
            """);
        var storedStates = decisions.GroupBy(row => (string)row[0]!).ToDictionary(group => group.Key, group => group.Count());
        var matcherVersions = decisions.GroupBy(row => (string)row[2]!).ToDictionary(group => group.Key, group => group.Count());
        var rerunGroups = Query(db, """
            SELECT count(*), sum(Protected) FROM (
                SELECT s.OwnerUserId, s.Protocol, s.BackendInstanceId, s.BackendPrincipalId, s.ProviderId, s.ExternalIdHash,
                       max(CASE WHEN EXISTS (SELECT 1 FROM manual_track_overrides o WHERE o.SourceProviderId=s.ProviderId
                           AND o.SourceExternalIdHash=s.ExternalIdHash AND o.RevokedAt IS NULL
                           AND (o.OwnerUserId IS NULL OR o.OwnerUserId=s.OwnerUserId)) THEN 1 ELSE 0 END) AS Protected
                FROM external_metadata_snapshots s JOIN users u ON u.Id=s.OwnerUserId AND u.Enabled=1
                    AND u.BackendInstanceId=s.BackendInstanceId AND u.BackendPrincipalId=s.BackendPrincipalId
                    AND u.BackendType=CASE WHEN s.Protocol='opensubsonic' THEN 'subsonic' ELSE s.Protocol END
                WHERE s.ResourceKind='track' AND s.ProviderId<>'allstarr' AND (s.ProviderAccountId IS NULL OR EXISTS (
                    SELECT 1 FROM provider_accounts a WHERE a.Id=s.ProviderAccountId AND a.Enabled=1
                    AND a.ProviderId=s.ProviderId AND (a.OwnerUserId IS NULL OR a.OwnerUserId=s.OwnerUserId)))
                GROUP BY s.OwnerUserId, s.Protocol, s.BackendInstanceId, s.BackendPrincipalId, s.ProviderId, s.ExternalIdHash
            )
            """)[0];
        var olderAccepted = Query(db, """
            WITH snapshots AS (
                SELECT s.*, row_number() OVER (
                    PARTITION BY s.OwnerUserId, s.Protocol, s.BackendInstanceId, s.BackendPrincipalId, s.ProviderId, s.ExternalIdHash, s.ResourceKind
                    ORDER BY s.SnapshotVersion DESC, s.RetrievedAt DESC, s.Id DESC) AS SourceRank
                FROM external_metadata_snapshots s
            ), latest AS (
                SELECT m.*, row_number() OVER (PARTITION BY m.ExternalSnapshotId ORDER BY m.DecisionVersion DESC) AS DecisionRank
                FROM track_matches m
            ), classified AS (
                SELECT m.MatcherVersion,
                    CASE
                    WHEN s.ResourceKind<>'track' THEN 'non_track_snapshot'
                    WHEN EXISTS (SELECT 1 FROM manual_track_overrides o WHERE o.SourceProviderId=s.ProviderId
                        AND o.SourceExternalIdHash=s.ExternalIdHash AND o.RevokedAt IS NULL
                        AND (o.OwnerUserId IS NULL OR o.OwnerUserId=s.OwnerUserId)) THEN 'manual'
                    WHEN EXISTS (SELECT 1 FROM recording_kept_routes k WHERE k.CanonicalRecordingId=m.CanonicalRecordingId
                        OR k.CanonicalRecordingId=(SELECT i.CanonicalRecordingId FROM provider_track_identities i WHERE i.Id=s.ProviderTrackIdentityId)) THEN 'kept_route'
                    WHEN s.SourceRank<>1 THEN 'superseded_snapshot'
                    WHEN NOT EXISTS (SELECT 1 FROM users u WHERE u.Id=s.OwnerUserId AND u.Enabled=1
                        AND u.BackendInstanceId=s.BackendInstanceId AND u.BackendPrincipalId=s.BackendPrincipalId
                        AND u.BackendType=CASE WHEN s.Protocol='opensubsonic' THEN 'subsonic' ELSE s.Protocol END)
                        OR s.ProviderAccountId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM provider_accounts a
                        WHERE a.Id=s.ProviderAccountId AND a.Enabled=1 AND a.ProviderId=s.ProviderId
                        AND (a.OwnerUserId IS NULL OR a.OwnerUserId=s.OwnerUserId)) THEN 'inactive_scope'
                    ELSE 'current_automatic' END AS Reason
                FROM latest m JOIN snapshots s ON s.Id=m.ExternalSnapshotId
                WHERE m.DecisionRank=1 AND m.State='Accepted'
                    AND m.MatcherVersion<>'accepted-routing-v27'
            ) SELECT MatcherVersion, Reason, count(*) FROM classified GROUP BY MatcherVersion, Reason
            """).Select(row => new { version = (string)row[0]!, reason = (string)row[1]!, count = Convert.ToInt64(row[2]) }).ToArray();
        var missingArtistInputs = Query(db, "SELECT ProviderId, PayloadJson FROM external_metadata_snapshots WHERE ResourceKind='track' AND PayloadJson LIKE '%Wicked Game%'")
            .Select(row =>
            {
                using var document = JsonDocument.Parse((string)row[1]!);
                return new
                {
                    provider = (string)row[0]!,
                    title = Text(document.RootElement, "title", "Title", "name", "Name"),
                    artist = TrackSnapshotMetadata.Artist(document.RootElement)
                };
            }).Distinct().ToArray();
        var latestReview = new List<object>();
        var sourceKinds = Query(db, "SELECT ResourceKind, ProviderId, count(*) FROM external_metadata_snapshots GROUP BY ResourceKind, ProviderId")
            .Select(row => new { kind = (string)row[0]!, provider = (string)row[1]!, count = Convert.ToInt64(row[2]) }).ToArray();
        var history = tables.ContainsKey("listening_events")
            ? Query(db, "SELECT SourceKind, count(*), sum(CASE WHEN CanonicalRecordingId IS NOT NULL THEN 1 ELSE 0 END) FROM listening_events GROUP BY SourceKind")
                .Select(row => new { kind = (string)row[0]!, count = Convert.ToInt64(row[1]), resolved = Convert.ToInt64(row[2]) }).ToArray() : [];
        foreach (var row in decisions)
        {
            Count("stored_decisions");
            if ((string)row[0]! == "Pinned" || ((string)row[5]!).Contains("manual_override", StringComparison.Ordinal))
            {
                Count("manual_decisions_excluded");
                continue;
            }
            using var storedJson = JsonDocument.Parse((string)row[1]!);
            var rawScores = storedJson.RootElement.EnumerateArray().ToArray();
            var stored = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>((string)row[1]!, JsonOptions)!;
            if (stored.Length == 0) { Count("empty_candidate_evidence"); continue; }
            var retained = rawScores[0];
            using var sourceJson = JsonDocument.Parse((string?)row[3] ?? "{}");
            var root = sourceJson.RootElement;
            var retainedSource = Property(retained, "source") is { ValueKind: JsonValueKind.Object } savedSource
                ? savedSource.Deserialize<ExternalTrackMatchSnapshot>(JsonOptions) : null;
            if (row[3] == null && retainedSource == null) { Count("missing_source"); continue; }
            var title = retainedSource?.Title ?? Text(root, "title", "name");
            var artist = retainedSource?.Artist ?? TrackSnapshotMetadata.Artist(root);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            {
                Count("missing_source_labels");
                continue;
            }
            var source = retainedSource ?? new ExternalTrackMatchSnapshot("source", (string)row[4]!, (string)row[7]!, title, artist,
                Text(root, "album"), Text(root, "albumArtist"), Duration(root), Text(root, "isrc"),
                TrackSnapshotMetadata.MusicBrainzRecordingId(root), TrackSnapshotMetadata.IsExplicit(root));
            source = source with { DurationProvenance = source.DurationProvenance ?? TrackSnapshotMetadata.DurationProvenance(root) };
            var baselineSource = retainedSource ?? source with { IsExplicit = ReplayBaselineSnapshotMetadata.IsExplicit(root) };
            if (retainedSource != null) Count("retained_source_used");
            if (baselineSource.IsExplicit != source.IsExplicit) Count("source_explicitness_numeric_fix_difference");
            Count("with_candidate_evidence");
            var truncated = Property(retained, "candidatesTruncated") is { ValueKind: JsonValueKind.True } ||
                Property(retained, "candidatesTruncated") == null && stored.Length >= 20;
            if (truncated) Count("possibly_truncated_candidate_sets");
            if (source.IsExplicit == null) Count("unknown_source_explicitness");
            if (source.DurationMilliseconds is not > 0) Count("missing_source_duration");
            var priorities = stored.Where(score => !score.IsLocal && score.Components?.GetValueOrDefault("routingPriority") is > 0 and < int.MaxValue)
                .SelectMany(score => (score.ProviderTrackIds ?? new Dictionary<string, string>()).Keys.Select(provider =>
                    (Provider: NormalizeProvider(provider), Rank: (int)score.Components!["routingPriority"])))
                .Distinct().ToArray();
            var rowOrder = order;
            var savedPriority = Property(retained, "providerPriority");
            if (savedPriority.HasValue)
            {
                rowOrder = savedPriority.Value.ValueKind == JsonValueKind.Array
                    ? savedPriority.Value.Deserialize<string[]>(JsonOptions)! : [];
                Count("exact_retained_provider_priority_used");
            }
            else if (priorities.Length > 0 && priorities.Max(item => item.Rank) <= 100 &&
                priorities.GroupBy(item => item.Rank).All(group => group.Count() == 1))
            {
                rowOrder = Enumerable.Range(1, priorities.Max(item => item.Rank)).Select(rank =>
                    priorities.FirstOrDefault(item => item.Rank == rank).Provider ?? $"unobserved-{rank}").ToArray();
                Count("retained_provider_priority_used");
            }
            else if (stored.Any(score => !score.IsLocal)) Count("configured_provider_priority_fallback");
            var savedPolicy = Property(retained, "policy");
            var rowPolicy = savedPolicy.HasValue
                ? savedPolicy.Value.ValueKind == JsonValueKind.Object
                    ? savedPolicy.Value.Deserialize<TrackMatchPolicy>(JsonOptions)! : new TrackMatchPolicy()
                : policy;
            if (savedPolicy.HasValue) Count("retained_policy_used");
            current = new ReplayBaselineEngine(rowPolicy);
            candidate = new TrackMatchDecisionEngine(rowPolicy);
            var decided = new DateTimeOffset(Convert.ToInt64(row[8]), TimeSpan.Zero);
            var candidates = new List<LocalTrackMatchCandidate>();
            var baselineCandidates = new List<LocalTrackMatchCandidate>();
            var evidenceGaps = new HashSet<string>(StringComparer.Ordinal);
            if (truncated) evidenceGaps.Add("candidate_set_truncated");
            var inputOrder = stored.Select((score, index) => (score, raw: rawScores[index], index))
                .OrderBy(item => Property(item.raw, "candidateOrder") is { ValueKind: JsonValueKind.Number } sequence
                    ? sequence.GetInt32() : item.index).ToArray();
            if (inputOrder.All(item => Property(item.raw, "candidateOrder") is { ValueKind: JsonValueKind.Number }))
                Count("retained_candidate_order_used");
            else evidenceGaps.Add("original_candidate_order_not_retained");
            foreach (var input in inputOrder)
            {
                var score = input.score;
                var raw = input.raw;
                if (string.IsNullOrWhiteSpace(score.Title) || string.IsNullOrWhiteSpace(score.Artist))
                {
                    evidenceGaps.Add("candidate_labels_missing");
                    continue;
                }
                bool? explicitness = null;
                bool? baselineExplicitness = null;
                string? albumArtist = null;
                Guid? canonical = null;
                string? recordingId = null;
                foreach (var id in score.IsLocal ? new Dictionary<string, string>() : score.ProviderTrackIds ?? new Dictionary<string, string>())
                {
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value))).ToLowerInvariant();
                    var priorMetadata = metadata.GetValueOrDefault((NormalizeProvider(id.Key), hash))?.FirstOrDefault(item =>
                        item.Retrieved <= decided && Text(item.Root, "title", "name") == score.Title &&
                        TrackSnapshotMetadata.Artist(item.Root) == score.Artist && Text(item.Root, "album") == score.Album &&
                        (!Duration(item.Root).HasValue || !score.DurationMilliseconds.HasValue || Math.Abs(Duration(item.Root)!.Value - score.DurationMilliseconds.Value) <= 1000));
                    if (priorMetadata is not { Root.ValueKind: JsonValueKind.Object }) continue;
                    explicitness = TrackSnapshotMetadata.IsExplicit(priorMetadata.Value.Root);
                    baselineExplicitness = explicitness;
                    albumArtist = Text(priorMetadata.Value.Root, "albumArtist");
                    if (explicitness.HasValue) break;
                }
                if (score.IsLocal && library.TryGetValue(score.LibraryTrackId, out var local) &&
                    new DateTimeOffset(Convert.ToInt64(local[4]), TimeSpan.Zero) <= decided && (string)local[5]! == score.Title &&
                    (string)local[6]! == score.Artist && (string?)local[7] == score.Album &&
                    (local[8] == null ? null : (long?)Convert.ToInt64(local[8])) == score.DurationMilliseconds)
                {
                    canonical = local[1] == null ? null : Guid.Parse((string)local[1]!);
                    albumArtist = (string?)local[2];
                    recordingId = (string?)local[3];
                    Count("local_input_recovered_from_unchanged_library_row");
                }
                else if (score.IsLocal && new[] { "canonicalRecordingId", "albumArtist", "musicBrainzRecordingId" }
                    .Any(key => Property(raw, key) == null)) evidenceGaps.Add("local_recording_identity_not_retained");
                if (explicitness.HasValue) Count("candidate_explicitness_recovered_from_prior_metadata");
                var savedExplicitness = Property(raw, "isExplicit");
                if (savedExplicitness.HasValue)
                {
                    explicitness = savedExplicitness.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? savedExplicitness.Value.GetBoolean() : null;
                    baselineExplicitness = explicitness;
                    Count("retained_candidate_explicitness_used");
                }
                else if (score.Reasons.Contains("clean_edit_of_explicit") && !FuzzyMatcher.HasCleanRecordingLabel(score.Title))
                    baselineExplicitness = explicitness = false;
                else if (score.Reasons.Contains("clean_version_available")) baselineExplicitness = explicitness = true;
                else if (!score.IsLocal && !explicitness.HasValue) evidenceGaps.Add("candidate_explicitness_not_retained");
                if (Property(raw, "albumArtist") is { } savedArtist)
                    albumArtist = savedArtist.ValueKind == JsonValueKind.String ? savedArtist.GetString() : null;
                else if (albumArtist == null && score.Components?.ContainsKey("albumArtist") == true) evidenceGaps.Add("candidate_album_artist_not_retained");
                if (Property(raw, "musicBrainzRecordingId") is { } savedRecording)
                    recordingId = savedRecording.ValueKind == JsonValueKind.String ? savedRecording.GetString() : null;
                else if (recordingId == null && score.Reasons.Contains("musicbrainz_recording_id_exact")) evidenceGaps.Add("candidate_recording_id_not_retained");
                if (Property(raw, "canonicalRecordingId") is { } savedCanonical)
                    canonical = savedCanonical.ValueKind == JsonValueKind.String ? savedCanonical.GetGuid() : null;
                else if (score.Reasons.Contains("canonical_recording_id_exact")) evidenceGaps.Add("canonical_identity_not_retained");
                if (retainedSource == null && score.Reasons.Contains("odesli_alias_exact")) evidenceGaps.Add("source_aliases_not_retained");
                if (baselineExplicitness != explicitness) Count("candidate_explicitness_numeric_fix_difference");
                candidates.Add(new LocalTrackMatchCandidate(score.LibraryTrackId, Owner, "backend", "music",
                    score.BackendItemId, canonical, score.Title, score.Artist, score.Album, albumArtist,
                    score.DurationMilliseconds, score.CandidateIsrc, recordingId, explicitness,
                    score.ProviderTrackIds, score.IsLocal, score.IsBestVersion,
                    score.IsLocal ? null : score.ProviderTrackIds?.Keys.FirstOrDefault()));
                baselineCandidates.Add(candidates[^1] with { IsExplicit = baselineExplicitness });
            }
            if (candidates.Count != stored.Length || candidates.Select(item => item.LibraryTrackId).Distinct().Count() != candidates.Count)
            {
                Count("incomplete_candidate_set_excluded");
                continue;
            }
            var scope = Scope();
            var before = current.Decide(scope, baselineSource, baselineCandidates, providerPriority: rowOrder);
            var after = candidate.Decide(scope, source, candidates, providerPriority: rowOrder);
            if (evidenceGaps.Contains("candidate_explicitness_not_retained"))
            {
                var alternatives = Enumerable.Range(0, 4).Select(mode => candidates.Select((item, index) =>
                    item.IsLocal || item.IsExplicit.HasValue ? item : item with
                    { IsExplicit = mode == 0 || mode > 1 && index % 2 == mode % 2 }).ToArray()).ToArray();
                if (alternatives.Any(items => Different(current.Decide(scope, baselineSource, items, providerPriority: rowOrder), before))) Count("current_explicitness_sensitivity");
                if (alternatives.Any(items => Different(candidate.Decide(scope, source, items, providerPriority: rowOrder), after))) Count("candidate_explicitness_sensitivity");
            }
            Count("replayed");
            var latest = Convert.ToInt64(row[6]) == 1;
            if (latest) Count("latest_replayed");
            foreach (var gap in evidenceGaps) Count(gap);
            if (evidenceGaps.Count == 0) Count("no_detected_input_gaps");
            var scoresAgree = before.Candidates.All(score => stored.Any(previous => previous.LibraryTrackId == score.LibraryTrackId && Math.Abs(previous.Confidence - score.Confidence) < .00001));
            var storedSelection = (string)row[0]! is "Accepted" or "Suggested" ? (Guid?)stored[0].LibraryTrackId : null;
            var selectionAgrees = before.SelectedLibraryTrackId == storedSelection;
            if (scoresAgree) Count("stored_scores_reproduced");
            if (before.State.ToString() == (string)row[0]!) Count("stored_state_reproduced");
            if (selectionAgrees) Count("stored_selection_reproduced");
            Measure("current", before);
            Measure("candidate", after);
            if (scoresAgree && selectionAgrees && before.State.ToString() == (string)row[0]!)
            {
                Count("calibrated_replayed");
                Measure("calibrated_current", before);
                Measure("calibrated_candidate", after);
            }
            if (latest) { Measure("latest_current", before); Measure("latest_candidate", after); }
            var chosen = candidates.FirstOrDefault(item => item.LibraryTrackId == after.SelectedLibraryTrackId);
            var best = after.Candidates.FirstOrDefault();
            var top = best == null ? null : candidates.First(item => item.LibraryTrackId == best.LibraryTrackId);
            var originalTop = candidates.FirstOrDefault(item => item.LibraryTrackId == before.Candidates.FirstOrDefault()?.LibraryTrackId);
            var acceptedInputs = after.Candidates.Where(score => TrackMatchDecisionEngine.IsAcceptedExternalCandidate(score, after.AcceptThreshold))
                .Select(score => candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)).ToArray();
            var previousChosen = candidates.FirstOrDefault(item => item.LibraryTrackId == before.SelectedLibraryTrackId);
            var previousAfterScore = after.Candidates.FirstOrDefault(item => item.LibraryTrackId == before.SelectedLibraryTrackId);
            var previousAfterQualified = previousAfterScore != null &&
                TrackMatchDecisionEngine.IsAcceptedExternalCandidate(previousAfterScore, after.AcceptThreshold);
            var selectionContext = acceptedInputs.Any(item => item.IsLocal) ? "local_precedence" :
                acceptedInputs.Length == 0 ? "below_acceptance" :
                previousChosen != null && !previousAfterQualified ? "previous_choice_failed_production_qualification" :
                previousChosen != null && chosen != null && Rank(chosen, rowOrder) < Rank(previousChosen, rowOrder)
                    ? "accepted_provider_priority" :
                originalTop != null && top != null && TrackMatchDecisionEngine.SameAlbumEdition(source.Album, top.Album) == true &&
                TrackMatchDecisionEngine.SameAlbumEdition(source.Album, originalTop.Album) != true
                    ? "same_provider_album_edition_tiebreak" : "accepted_order_or_ambiguity_change";
            var operativeReason = baselineSource.IsExplicit != source.IsExplicit ? "source_numeric_explicitness_fix" :
                previousChosen != null && baselineCandidates.First(item => item.LibraryTrackId == previousChosen.LibraryTrackId).IsExplicit != previousChosen.IsExplicit
                    ? "candidate_numeric_explicitness_fix" :
                previousChosen != null && !previousAfterQualified && !SameVersionMarkers(source, previousChosen)
                    ? "known_recording_version_mismatch" :
                previousChosen != null && !previousAfterQualified && !FuzzyMatcher.TitleAnnotationsMatch(source.Title, source.Artist, previousChosen.Title, previousChosen.Artist)
                    ? "protected_title_annotation" :
                previousChosen != null && !previousAfterQualified
                    ? "previous_choice_failed_qualification/" + string.Join("+", previousAfterScore?.Reasons.Where(reason =>
                        reason is "duration_mismatch" or "isrc_conflict" or "clean_edit_of_explicit" or "clean_version_available" or
                            "continuous_mix_excerpt" or "backing_recording_mismatch" or "cover_artist_mismatch") ?? []) :
                previousChosen != null && chosen != null && Rank(chosen, rowOrder) < Rank(previousChosen, rowOrder)
                    ? "accepted_provider_priority" : selectionContext;
            var conflicts = ConflictingKnownFacts(source, candidates.ToArray()).ToArray();
            var ambiguousConflicts = after.State == TrackMatchReviewState.Ambiguous && best != null
                ? ConflictingKnownFacts(source, after.Candidates.Where(score =>
                    best.Confidence - score.Confidence <= after.AmbiguityDelta &&
                    score.Components?.GetValueOrDefault("routingPriority") == best.Components?.GetValueOrDefault("routingPriority") &&
                    score.Components?.GetValueOrDefault("recordingAllowed", 1) != 0)
                    .Select(score => candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)).ToArray()).ToArray()
                : [];
            var example = new
            {
                source = Metadata(source.Title, source.Artist, source.Album, source.ProviderId, null, source.IsExplicit, source.DurationMilliseconds, source.Isrc),
                source.DurationProvenance,
                baselineSourceExplicitness = baselineSource.IsExplicit,
                productionSourceExplicitness = source.IsExplicit,
                current = Describe(before, baselineCandidates),
                candidate = Describe(after, candidates),
                selectionContext,
                operativeReason,
                previousChoiceProductionQualified = previousAfterQualified,
                previousChoiceProductionReasons = previousAfterScore?.Reasons,
                previousChoiceProductionWarnings = previousAfterScore?.Warnings,
                previousChoiceProductionComponents = previousAfterScore?.Components,
                conflictingKnownFacts = conflicts,
                ambiguousConflictingKnownFacts = ambiguousConflicts,
                priority = rowOrder.Where(provider => !provider.StartsWith("unobserved-", StringComparison.Ordinal)).ToArray(),
                candidates = candidates.Select(item => Metadata(item.Title, item.Artist, item.Album, Provider(item),
                    after.Candidates.FirstOrDefault(score => score.LibraryTrackId == item.LibraryTrackId)?.Confidence,
                    item.IsExplicit, item.DurationMilliseconds, item.Isrc)).ToArray(),
                candidateQualification = after.Candidates.Select(score => new
                {
                    score.Title,
                    score.Artist,
                    score.Album,
                    provider = Provider(candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)),
                    score.Confidence,
                    score.Reasons,
                    score.Warnings,
                    score.Components
                }).ToArray(),
                baselineCandidateExplicitness = baselineCandidates.Select(item => new
                { provider = Provider(item), item.Title, item.Album, item.IsExplicit }).ToArray(),
                evidenceGaps = evidenceGaps.Order().ToArray(),
                storedScoresReproduced = scoresAgree
            };
            if (latest && after.State != TrackMatchReviewState.Accepted) latestReview.Add(example);
            if (previousChosen != null && chosen != null && Provider(previousChosen) != Provider(chosen))
            {
                Add("provider_change/all");
                Add("provider_change/" + operativeReason);
            }
            if (before.State == TrackMatchReviewState.Accepted && previousChosen != null &&
                Provider(previousChosen) == "apple-download" && Rank(previousChosen, rowOrder) == 0 &&
                chosen != null && !chosen.IsLocal && Rank(chosen, rowOrder) > 0)
            {
                Add("apple_rank1_accepted_to_lower_priority/all");
                Add("apple_rank1_accepted_to_lower_priority/" + operativeReason);
                if (previousAfterQualified) Add("apple_rank1_accepted_to_lower_priority/still_qualified_contradiction");
                foreach (var reason in previousAfterScore?.Reasons.Where(reason => reason is
                    "clean_edit_of_explicit" or "clean_version_available" or "duration_mismatch" or "isrc_conflict") ?? [])
                    Add("apple_rank1_accepted_to_lower_priority/production_reason_" + reason);
            }
            if (before.State != after.State || before.SelectedLibraryTrackId != after.SelectedLibraryTrackId ||
                !before.Candidates.Select(item => item.LibraryTrackId).SequenceEqual(after.Candidates.Select(item => item.LibraryTrackId)))
            {
                var reason = before.State != after.State ? $"state_{before.State}_to_{after.State}" :
                    before.SelectedLibraryTrackId != after.SelectedLibraryTrackId ? "selected_recording_changed" : "candidate_order_only";
                var previous = candidates.FirstOrDefault(item => item.LibraryTrackId == before.SelectedLibraryTrackId);
                if (previous != null && chosen != null && Provider(previous) != Provider(chosen)) reason += "/provider_changed";
                else if (previous != null && chosen != null && TrackMatchDecisionEngine.SameAlbumEdition(source.Album, chosen.Album) == true &&
                         TrackMatchDecisionEngine.SameAlbumEdition(source.Album, previous.Album) != true) reason += "/same_album_edition";
                if (!groups.TryGetValue(reason, out var list)) groups[reason] = list = [];
                list.Add(example);
                Song("change/" + reason);
                changes.Add(new { reason, latest, detail = example });
            }
            if (after.State == TrackMatchReviewState.Ambiguous)
            {
                var tied = after.Candidates.Where(score => best!.Confidence - score.Confidence <= after.AmbiguityDelta &&
                    score.Components?.GetValueOrDefault("routingPriority") == best.Components?.GetValueOrDefault("routingPriority") &&
                    score.Components?.GetValueOrDefault("recordingAllowed", 1) != 0 &&
                    (!SourceVariant(source, top!) || SourceVariant(source, candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)))).ToArray();
                Add("ambiguous/all");
                foreach (var conflict in ambiguousConflicts) Add("ambiguous/conflicting_known_fact/" + conflict);
                if (tied.GroupBy(score => Provider(candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)))
                    .Any(group => group.Count() > 1)) Add("ambiguous/same_provider_duplicate_editions");
                if (source.IsExplicit == null) Add("ambiguous/unknown_source_explicitness");
                foreach (var tag in Tags(source, tied.Select(score => candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)).ToArray())) Add("ambiguous/" + tag);
            }
            if (chosen != null && after.State == TrackMatchReviewState.Accepted)
            {
                if (source.IsExplicit.HasValue && chosen.IsExplicit.HasValue && source.IsExplicit != chosen.IsExplicit) Add("suspected_wrong/explicitness_mismatch");
                if (!SameVersionMarkers(source, chosen)) Add("suspected_wrong/version_markers");
                else if (!SameVersions(source, chosen)) Add("review/annotation_difference");
                if (source.DurationMilliseconds is > 0 && chosen.DurationMilliseconds is > 0 &&
                    Math.Abs(source.DurationMilliseconds.Value - chosen.DurationMilliseconds.Value) > 8000) Add("suspected_wrong/duration_outlier");
                if (TrackMatchSignals.ArtistSimilarity(source.Artist, chosen.Artist) < .7) Add("suspected_wrong/different_artist");
                if (source.IsExplicit.HasValue && chosen.IsExplicit == null) Add("unverifiable_pick/candidate_explicitness_missing");
            }
            var acceptable = after.Candidates.Where(score => Acceptable(source, candidates.First(item => item.LibraryTrackId == score.LibraryTrackId), score, after.AcceptThreshold)).ToArray();
            var highest = acceptable.Where(score => !score.IsLocal).Select(score => Rank(candidates.First(item => item.LibraryTrackId == score.LibraryTrackId), rowOrder)).DefaultIfEmpty(int.MaxValue).Min();
            if (chosen != null && !chosen.IsLocal && highest != int.MaxValue && Rank(chosen, rowOrder) > highest)
                Add("lower_priority/acceptable_same_variant_available");
            else if (chosen != null && !chosen.IsLocal && Rank(chosen, rowOrder) > 0)
            {
                var higher = after.Candidates.Where(score => !score.IsLocal && Rank(candidates.First(item => item.LibraryTrackId == score.LibraryTrackId), rowOrder) < Rank(chosen, rowOrder)).ToArray();
                if (higher.Length == 0) Add("lower_priority/no_higher_provider_evidence");
                else
                {
                    Add("lower_priority/higher_provider_failed_qualification");
                    foreach (var reason in higher.SelectMany(score => score.Reasons).Where(reason => reason is
                        "duration_mismatch" or "isrc_conflict" or "clean_edit_of_explicit" or "clean_version_available" or "backing_recording_mismatch" or "cover_artist_mismatch").Distinct())
                        Add("lower_priority/rejected_" + reason);
                    foreach (var tag in Tags(source, higher.Select(score => candidates.First(item => item.LibraryTrackId == score.LibraryTrackId)).ToArray())) Add("lower_priority/" + tag);
                }
            }

            void Add(string key)
            {
                Count(key);
                var newSong = Song(key);
                if (!examples.TryGetValue(key, out var list)) examples[key] = list = [];
                if (list.Count < 10 && newSong) list.Add(example);
            }

            bool Song(string key)
            {
                if (!affectedSongs.TryGetValue(key, out var songs)) affectedSongs[key] = songs = new HashSet<string>(StringComparer.Ordinal);
                return songs.Add(source.Title + "\n" + source.Artist + "\n" + source.Album);
            }

            object Describe(TrackMatchDecision decision, IReadOnlyList<LocalTrackMatchCandidate> inputs)
            {
                var selected = inputs.FirstOrDefault(item => item.LibraryTrackId == decision.SelectedLibraryTrackId);
                return new { state = decision.State.ToString(), selected = selected == null ? null : Metadata(selected.Title, selected.Artist, selected.Album, Provider(selected), decision.Confidence, selected.IsExplicit, selected.DurationMilliseconds, selected.Isrc), decision.Confidence, decision.Reasons, decision.Warnings };
            }

            void Measure(string prefix, TrackMatchDecision decision)
            {
                Count(prefix + "/" + decision.State);
                if (decision.RequiresReview) Count(prefix + "/needs_review");
                var measureSource = prefix.EndsWith("current", StringComparison.Ordinal) || prefix == "current" ? baselineSource : source;
                var measureInputs = prefix.EndsWith("current", StringComparison.Ordinal) || prefix == "current" ? baselineCandidates : candidates;
                var selected = measureInputs.FirstOrDefault(item => item.LibraryTrackId == decision.SelectedLibraryTrackId);
                if (selected != null) Count(prefix + "/chosen/" + Provider(selected));
                if (selected != null && decision.State == TrackMatchReviewState.Accepted) Count(prefix + "/accepted_chosen/" + Provider(selected));
                var eligible = decision.Candidates.Where(score => Acceptable(measureSource, measureInputs.First(item => item.LibraryTrackId == score.LibraryTrackId), score, decision.AcceptThreshold) && !score.IsLocal).ToArray();
                if (eligible.Length == 0) return;
                Count(prefix + "/priority_opportunities");
                var highestRank = eligible.Min(score => Rank(candidates.First(item => item.LibraryTrackId == score.LibraryTrackId), rowOrder));
                if (highestRank == int.MaxValue) { Count(prefix + "/priority_rank_unknown"); return; }
                if (selected != null && !selected.IsLocal && Rank(selected, rowOrder) == highestRank) Count(prefix + "/priority_adherent");
                if (selected != null && !selected.IsLocal && decision.State == TrackMatchReviewState.Accepted)
                {
                    Count(prefix + "/accepted_priority_opportunities");
                    if (Rank(selected, rowOrder) == highestRank) Count(prefix + "/accepted_priority_adherent");
                }
                if (selected?.IsLocal == true) Count(prefix + "/local_precedence");
                if (selected == null) Count(prefix + "/priority_no_selection");
            }
        }
        return new
        {
            measuredAtUtc = DateTimeOffset.UtcNow,
            elapsedSeconds = watch.Elapsed.TotalSeconds,
            algorithm = TrackMatchDecisionEngine.AlgorithmVersion,
            baselineAlgorithm = ReplayBaselineEngine.AlgorithmVersion,
            baselineFingerprint = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "Matching", "baseline-manifest.json"))),
            metricLabels = new { current = "frozen origin/dev baseline", candidate = "production candidate" },
            explicitnessReconstruction = new { current = "frozen boolean-only helper for historical input", candidate = "production numeric-aware helper for source input; candidate flags reconstructed identically", retained = "exact retained values, including null, on both engines" },
            order,
            localPreferenceWindow = window,
            tables,
            sourceKinds,
            history,
            storedStates,
            matcherVersions,
            olderAccepted,
            missingArtistInputs,
            latestReview,
            rerunScopeUpperBound = new { groups = Convert.ToInt64(rerunGroups[0]), protectedManual = Convert.ToInt64(rerunGroups[1] ?? 0) },
            counts,
            affectedSongs = affectedSongs.ToDictionary(item => item.Key, item => item.Value.Count),
            changeGroups = groups.ToDictionary(item => item.Key, item => new { count = item.Value.Count, examples = item.Value.Take(10).ToArray() }),
            changes,
            taxonomyExamples = examples,
            chosenProviderChanges = Taxonomy("provider_change/"),
            appleRank1AcceptedToLowerPriority = Taxonomy("apple_rank1_accepted_to_lower_priority/"),
            remainingAmbiguousKnownConflicts = Taxonomy("ambiguous/conflicting_known_fact/"),
            corpus = EvaluateCorpus()
        };

        object Taxonomy(string prefix) => counts.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(item => item.Key[prefix.Length..], item => new
            {
                count = item.Value,
                distinctSongs = affectedSongs.GetValueOrDefault(item.Key)?.Count ?? 0,
                examples = examples.GetValueOrDefault(item.Key)?.ToArray() ?? []
            });

        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    internal static TrackMatchScope Scope() => new(Owner, "backend", null, 2, 1, new HashSet<string> { "music" });

    private static bool Different(TrackMatchDecision left, TrackMatchDecision right) =>
        left.State != right.State || left.SelectedLibraryTrackId != right.SelectedLibraryTrackId;

    internal static bool SameVersions(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        SameVersionMarkers(source, candidate) &&
        FuzzyMatcher.TitleAnnotationsMatch(source.Title, source.Artist, candidate.Title, candidate.Artist);

    private static bool SameVersionMarkers(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        TrackMatchSignals.RecordingVersionsMatch(source, candidate, 8);

    private static bool Acceptable(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate,
        TrackMatchCandidateScore score, double acceptThreshold) =>
        TrackMatchDecisionEngine.IsAcceptedExternalCandidate(score, acceptThreshold) && SourceVariant(source, candidate);

    private static bool SourceVariant(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        TrackMatchDecisionEngine.MatchesSourceVariant(source, candidate);

    private static IEnumerable<string> ConflictingKnownFacts(ExternalTrackMatchSnapshot source,
        IReadOnlyList<LocalTrackMatchCandidate> candidates)
    {
        if (candidates.Any(item => source.IsExplicit.HasValue && item.IsExplicit.HasValue && source.IsExplicit != item.IsExplicit))
            yield return "source_candidate_explicitness";
        if (candidates.Any(item => item.IsExplicit == true) && candidates.Any(item => item.IsExplicit == false))
            yield return "candidate_clean_vs_explicit";
        if (candidates.Any(item => source.DurationMilliseconds is > 0 && item.DurationMilliseconds is > 0 &&
            Math.Abs(source.DurationMilliseconds.Value - item.DurationMilliseconds.Value) > 8_000))
            yield return "source_candidate_duration";
        var durations = candidates.Where(item => item.DurationMilliseconds is > 0)
            .Select(item => item.DurationMilliseconds!.Value).ToArray();
        if (durations.Length > 1 && durations.Max() - durations.Min() > 8_000) yield return "candidate_duration";
        if (candidates.Any(item => !SameVersionMarkers(source, item))) yield return "recording_version_tags";
        if (candidates.Any(item => !FuzzyMatcher.TitleAnnotationsMatch(source.Title, source.Artist, item.Title, item.Artist)))
            yield return "title_annotations";
        if (candidates.Any(item => !string.IsNullOrWhiteSpace(source.Isrc) && !string.IsNullOrWhiteSpace(item.Isrc) &&
            !FuzzyMatcher.IdentifiersEqual(source.Isrc, item.Isrc))) yield return "source_candidate_isrc";
        if (candidates.Select(item => item.Isrc).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant()).Distinct().Skip(1).Any())
            yield return "candidate_isrc";
    }

    private static IEnumerable<string> Tags(ExternalTrackMatchSnapshot source, IReadOnlyList<LocalTrackMatchCandidate> candidates)
    {
        var text = string.Join(' ', candidates.Select(item => item.Title + " " + item.Album).Append(source.Title + " " + source.Album)).ToLowerInvariant();
        foreach (var tag in new[] { "live", "remix", "sped", "karaoke", "instrumental", "deluxe", "remaster", "anniversary", "extended" })
            if (text.Contains(tag, StringComparison.Ordinal) ||
                tag == "live" && (text.Contains("ao vivo", StringComparison.Ordinal) || text.Contains("en vivo", StringComparison.Ordinal)) ||
                tag == "sped" && (text.Contains("speed up", StringComparison.Ordinal) || text.Contains("speed-up", StringComparison.Ordinal))) yield return tag;
        if (candidates.Any(item => item.IsExplicit == true) && candidates.Any(item => item.IsExplicit == false)) yield return "clean_vs_explicit";
        if (text.Contains("feat", StringComparison.Ordinal) || candidates.Any(item => item.Artist.Contains(','))) yield return "featuring_artist_formatting";
        if (candidates.Any(item => item.Title != source.Title && FuzzyMatcher.NormalizeForMatching(item.Title) == FuzzyMatcher.NormalizeForMatching(source.Title))) yield return "title_punctuation_or_case";
        if (candidates.Any(item => source.DurationMilliseconds is > 0 && item.DurationMilliseconds is > 0 && Math.Abs(source.DurationMilliseconds.Value - item.DurationMilliseconds.Value) > 8000)) yield return "duration_outlier";
        if (source.DurationMilliseconds is not > 0 || candidates.Any(item => item.DurationMilliseconds is not > 0)) yield return "missing_duration";
        if (candidates.Any(item => !string.IsNullOrWhiteSpace(source.Isrc) && !string.IsNullOrWhiteSpace(item.Isrc) && !FuzzyMatcher.IdentifiersEqual(source.Isrc, item.Isrc))) yield return "isrc_conflict";
        if (candidates.Any(item => item.Title.Any(character => character > 127) && item.Title != source.Title)) yield return "title_localization_or_diacritics";
    }

    internal static string Provider(LocalTrackMatchCandidate item) => item.IsLocal ? "local" : NormalizeProvider(item.ProviderTrackIds?.Keys.FirstOrDefault() ?? "unknown");
    private static string NormalizeProvider(string value) => value is "apple-musickit" or "apple-music" ? "apple-download" : value;
    private static int Rank(LocalTrackMatchCandidate item, IReadOnlyList<string> order) => item.IsLocal ? -1 :
        order.Select((provider, index) => (provider, index)).Where(pair => NormalizeProvider(pair.provider) == Provider(item)).Select(pair => pair.index).DefaultIfEmpty(int.MaxValue).Min();

    private static object Metadata(string title, string artist, string? album, string provider, double? score, bool? explicitness, long? duration, string? isrc = null) =>
        new { title, artist, album, provider, score, isExplicit = explicitness, durationMilliseconds = duration, isrc };

    private static JsonElement? Property(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    internal static string? Text(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (keys.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static long? Duration(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number) continue;
            if (new[] { "durationMilliseconds", "durationMs" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return (long)Math.Round(property.Value.GetDouble());
        }
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Number && new[] { "durationSeconds", "duration" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return (long)Math.Round(property.Value.GetDouble() * 1000);
        return null;
    }

    private static string[] ReadOrder(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : document.RootElement.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static List<object?[]> Query(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++) row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    internal static object EvaluateCorpus()
    {
        var cases = LoadCorpus();
        var results = cases.Select(item =>
        {
            var scope = Scope();
            var source = item.Source.Snapshot();
            var candidates = item.Candidates.Select((song, index) => song.Candidate(index)).ToArray();
            var before = new ReplayBaselineEngine().Decide(scope, source, candidates, providerPriority: item.Priority);
            var after = new TrackMatchDecisionEngine().Decide(scope, source, candidates, providerPriority: item.Priority);
            bool Pass(TrackMatchDecision decision) => decision.State.ToString() == item.ExpectedState &&
                (item.ExpectedCandidate == null ? decision.SelectedLibraryTrackId == null : decision.SelectedLibraryTrackId == candidates[item.ExpectedCandidate.Value].LibraryTrackId);
            return new { item.Name, current = Pass(before), candidate = Pass(after), currentState = before.State.ToString(), candidateState = after.State.ToString() };
        }).ToArray();
        return new { total = results.Length, currentPassed = results.Count(item => item.current), candidatePassed = results.Count(item => item.candidate), results };
    }

    internal static ReplayCase[] LoadCorpus() => JsonSerializer.Deserialize<ReplayCase[]>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Matching", "reviewer-corpus.json")), JsonOptions)!;

    internal sealed record ReplayCase(string Name, string[] Priority, ReplaySong Source, ReplaySong[] Candidates, string ExpectedState, int? ExpectedCandidate);

    internal sealed record ReplaySong(string Title, string Artist, string? Album, string Provider, long? DurationMilliseconds, bool? IsExplicit, string? Isrc = null, string? DurationProvenance = null)
    {
        public ExternalTrackMatchSnapshot Snapshot() => new("source", Provider, "source", Title, Artist, Album, null, DurationMilliseconds, Isrc, null, IsExplicit)
        {
            DurationProvenance = DurationProvenance
        };

        public LocalTrackMatchCandidate Candidate(int index) => new(
            new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"candidate-{index}")).AsSpan(0, 16)),
            Owner, "backend", Provider == "local" ? "music" : null, $"candidate-{index}", null,
            Title, Artist, Album, null, DurationMilliseconds, Isrc, null, IsExplicit,
            Provider == "local" ? null : new Dictionary<string, string> { [Provider] = $"candidate-{index}" },
            IsLocal: Provider == "local", ExternalProvider: Provider);
    }
}
