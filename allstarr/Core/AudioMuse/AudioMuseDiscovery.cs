using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Enrichment;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.AudioMuse;

public sealed record AudioMuseSong(string Id, string Title, string Artist, string? Album);
public sealed record AudioMusePreview(string? Reason, AudioMuseSong[] Tracks, int EligibleCount = 0,
    Guid? PreviewId = null, string? RecipeVersion = null);
public sealed record AudioMuseStatus(string? Reason, int AccessibleLibrarySongs = 0, int AnalyzedSongs = 0, bool CoverageComplete = false)
{
    public string? CoverageMessage => Reason == null ? string.Create(CultureInfo.InvariantCulture,
        $"{AnalyzedSongs:N0} of your {AccessibleLibrarySongs:N0} library songs are analyzed") : null;
}
public sealed record AudioMuseSaved(string? Reason, string? PlaylistId = null);

public sealed class AudioMuseDiscovery(IDbContextFactory<AllstarrDbContext> factory, AudioMuseSettings settings,
    AudioMuseClient client, IBackendLibraryAccessResolver access, IEffectiveProviderPolicyResolver policies,
    IBackendLibraryPermissionSource permissions, IHttpClientFactory clients, IBackendPlaylistTargetResolver targets,
    IConfiguration configuration, SharedTagStore tags)
{
    private const string PreviewKind = "audiomuse-preview";
    private readonly SemaphoreSlim _save = new(1, 1);

    public async Task<AudioMuseStatus> StatusAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = token;
        token = deadline.Token;
        try
        {
            var (_, _, coverage) = await BoundAsync(context, token);
            await using var db = await factory.CreateDbContextAsync(token);
            var allowed = await access.ResolveAsync(context, token);
            var query = LibraryTrackAccess.Query(db, context, allowed);
            var count = await query.CountAsync(token);
            return new(null, count, coverage.Analyzed!.Value, coverage.Complete);
        }
        catch (Exception error) when (Unavailable(error, cancellationToken) is not null) { return new(Unavailable(error, cancellationToken)); }
    }

    public Task<AudioMusePreview> SearchAsync(ProtocolExecutionContext context, string query, CancellationToken cancellationToken) =>
        PreviewAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("Supply a song search of 1–200 characters.");
            var (endpoint, server, _) = await BoundAsync(context, token);
            var rows = await ResolveAsync(context, await client.SearchAsync(endpoint, server, query.Trim(), token), token);
            var analyzed = new List<LibraryTrackRecord>();
            foreach (var row in rows.Take(50))
                if (await client.AnalyzedAsync(endpoint, server, row.BackendItemId, token)) analyzed.Add(row);
            return new(null, analyzed.Select(Song).ToArray(), analyzed.Count);
        }, cancellationToken);

    public Task<AudioMusePreview> SimilarAsync(ProtocolExecutionContext context, string seedId, int n, CancellationToken cancellationToken) =>
        PreviewAsync(async token =>
        {
            CheckLimit(n);
            if (await settings.EndpointAsync(token) == null) throw new AudioMuseUnavailable("not_enabled");
            var seed = (await ResolveAsync(context, [new(seedId)], token)).SingleOrDefault();
            if (seed == null) throw new AudioMuseUnavailable("song_not_analyzed");
            var (endpoint, server, _) = await BoundAsync(context, token);
            if (!await client.TrackAsync(endpoint, server, seedId, token)) throw new AudioMuseUnavailable("song_not_analyzed");
            var rows = await ResolveAsync(context, await client.SimilarAsync(endpoint, server, seedId, null, token), token);
            var eligible = Select(rows, seed, 150, artistCap: false);
            return await PublishAsync(context, Select(eligible, seed, n), eligible.Length, null, token);
        }, cancellationToken);

    public Task<AudioMusePreview> MoodAsync(ProtocolExecutionContext context, string mood, int n, CancellationToken cancellationToken) =>
        PreviewAsync(async token =>
        {
            CheckLimit(n);
            if (mood is not ("calm" or "workout")) throw new ArgumentException("Choose calm or workout.");
            var (endpoint, server, _) = await BoundAsync(context, token);
            var anchor = mood == "calm" ? "relaxed" : "danceable";
            if (!await client.HasAnchorAsync(endpoint, anchor, token)) throw new AudioMuseUnavailable("not_set_up");
            var rows = await ResolveAsync(context, await client.SimilarAsync(endpoint, server, null, anchor, token), token);
            var scored = new List<(LibraryTrackRecord Track, double Score)>();
            foreach (var row in rows)
            {
                try
                {
                    var score = RecipeScore(mood, await client.ScoresAsync(endpoint, server, row.BackendItemId, token));
                    if (score.HasValue) scored.Add((row, score.Value));
                }
                catch (AudioMuseUnavailable error) when (error.Reason == "song_not_analyzed") { }
            }
            if (rows.Length > 0 && scored.Count == 0) throw new AudioMuseUnavailable("not_set_up");
            var eligible = Select(scored.OrderByDescending(s => s.Score).ThenBy(s => s.Track.BackendItemId, StringComparer.Ordinal)
                .Select(s => s.Track), null, 150, artistCap: false);
            return await PublishAsync(context, Select(eligible, null, n), eligible.Length, mood + "-v1", token);
        }, cancellationToken);

    public async Task<AudioMuseSaved> SaveAsync(ProtocolExecutionContext context, Guid previewId, string name, CancellationToken token)
    {
        if (context.Protocol != ProtocolKind.Jellyfin || context.BackendInstanceId != (configuration["Identity:BackendInstanceId"] ?? "primary") ||
            configuration["Backend:Type"] is { } backendType && !backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) throw new ArgumentException("Supply a playlist name of 1–200 characters.");
        await _save.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var owner = context.Principal?.UserId ?? throw new UnauthorizedAccessException();
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(r => r.Id == previewId && r.OwnerUserId == owner &&
                r.Protocol == "jellyfin" && r.BackendInstanceId == context.BackendInstanceId &&
                r.BackendPrincipalId == context.VerifiedBackendPrincipalId && r.ProviderId == "allstarr" && r.ResourceKind == PreviewKind, token)
                ?? throw new UnauthorizedAccessException();
            var latest = await db.ExternalMetadataSnapshots.AsNoTracking().Where(r => r.OwnerUserId == owner &&
                r.Protocol == row.Protocol && r.BackendInstanceId == row.BackendInstanceId && r.BackendPrincipalId == row.BackendPrincipalId &&
                r.ProviderId == "allstarr" && r.ResourceKind == PreviewKind && r.ExternalIdHash == row.ExternalIdHash)
                .OrderByDescending(r => r.SnapshotVersion).FirstAsync(token);
            var saved = JsonSerializer.Deserialize<StoredPreview>(latest.PayloadJson)!;
            if (saved.PlaylistId != null) return new(null, saved.PlaylistId);
            if (row.RetrievedAt.AddMinutes(30) < DateTimeOffset.UtcNow) throw new ArgumentException("Preview expired. Generate another preview.");
            var rows = await ResolveAsync(context, saved.Ids.Select(id => new AudioMuseTrack(id)).ToArray(), token);
            if (rows.Length == 0) return new("too_little_analyzed_music");
            var result = await targets.Resolve("jellyfin").WriteAsync(new(context.BackendInstanceId, context.VerifiedBackendPrincipalId),
                new(BackendPlaylistWriteMode.Recreate, new(name.Trim()) { Private = true },
                    rows.Select(r => r.BackendItemId), previewId.ToString("N")), token);
            if (!result.IsSuccess) return new("playlist_save_failed");
            saved = saved with { PlaylistId = result.Value!.Snapshot.BackendPlaylistId };
            var payload = JsonSerializer.Serialize(saved);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                Protocol = row.Protocol,
                BackendInstanceId = row.BackendInstanceId,
                BackendPrincipalId = row.BackendPrincipalId,
                ProviderId = "allstarr",
                ResourceKind = PreviewKind,
                ExternalIdHash = row.ExternalIdHash,
                SnapshotVersion = latest.SnapshotVersion + 1,
                ProviderRevision = row.ProviderRevision,
                PayloadJson = payload,
                PayloadSha256 = Hash(payload),
                CorrelationId = context.CorrelationId,
                RetrievedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(token);
            return new(null, saved.PlaylistId);
        }
        finally { _save.Release(); }
    }

    private async Task<(AudioMuseEndpoint Endpoint, string Server, AudioMuseCoverage Coverage)> BoundAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        var endpoint = await settings.EndpointAsync(token) ?? throw new AudioMuseUnavailable("not_enabled");
        if (context.Protocol != ProtocolKind.Jellyfin ||
            configuration["Backend:Type"] is { } backendType && !backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase) || context.BackendInstanceId != (configuration["Identity:BackendInstanceId"] ?? "primary"))
            throw new AudioMuseUnavailable("not_set_up");
        if (!await client.HealthAsync(endpoint, token)) throw new AudioMuseUnavailable("not_reachable");
        if ((await client.VersionAsync(endpoint, token))?.TrimStart('v') != "3.6.4") throw new AudioMuseUnavailable("not_set_up");
        var servers = (await client.ServersAsync(endpoint, token)).Where(s => s.Type.Equals("jellyfin", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (servers.Length > 1)
            servers = servers.Where(s => AudioMuseSettings.TryBaseUri(s.Url, out var uri) &&
                AudioMuseSettings.TryBaseUri(configuration["Jellyfin:Url"], out var backend) && uri == backend).ToArray();
        if (servers.Length != 1 || string.IsNullOrWhiteSpace(servers[0].Id)) throw new AudioMuseUnavailable("not_set_up");
        var coverage = await client.CoverageAsync(endpoint, servers[0].Id, token);
        if (coverage.ServerId != servers[0].Id || !coverage.Enabled || coverage.Analyzed == null || coverage.Total == null ||
            coverage.Analyzed > coverage.Total) throw new AudioMuseUnavailable("not_set_up");
        if (coverage.Analyzed == 0) throw new AudioMuseUnavailable("too_little_analyzed_music");
        return (endpoint, servers[0].Id, coverage);
    }

    private async Task<LibraryTrackRecord[]> ResolveAsync(ProtocolExecutionContext context, AudioMuseTrack[] candidates, CancellationToken token)
    {
        var ids = candidates.Select(c => c.Id).Where(id => id.Length is > 0 and <= 200 && !id.StartsWith("fp_", StringComparison.OrdinalIgnoreCase) &&
            !id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Take(150).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var allowed = await access.ResolveAsync(context, token);
        var rows = await LibraryTrackAccess.Query(db, context, allowed).Where(r => ids.Contains(r.BackendItemId)).ToArrayAsync(token);
        var byId = rows.GroupBy(r => r.BackendItemId, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        if (context.Principal == null) throw new UnauthorizedAccessException();
        if ((await policies.ResolveForUserAsync(context.Principal.UserId, token)).Preferences.ExplicitFilter == "CleanOnly" && byId.Count > 0)
        {
            using var request = await permissions.CreateRequestAsync(context, token);
            if (request == null) return [];
            request.RequestUri = new(new Uri(configuration["Jellyfin:Url"]!.TrimEnd('/') + "/"),
                "Items?UserId=" + Uri.EscapeDataString(context.VerifiedBackendPrincipalId) + "&IncludeItemTypes=Audio&Recursive=true&Ids=" +
                Uri.EscapeDataString(string.Join(',', byId.Keys)) + "&Fields=OfficialRating");
            request.Method = HttpMethod.Get;
            using var http = clients.CreateClient(JellyfinProxyService.HttpClientName);
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return [];
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
            var clean = (root?["Items"] as JsonArray ?? []).OfType<JsonObject>().Where(r =>
                CleanOnlyProtocolContent.Text(r, "Type") == "Audio" && CleanOnlyProtocolContent.Rating(r, ProtocolKind.Jellyfin) is 0 or 3)
                .Select(r => CleanOnlyProtocolContent.Text(r, "Id")).ToHashSet(StringComparer.Ordinal);
            byId = byId.Where(p => clean.Contains(p.Key)).ToDictionary();
        }
        foreach (var candidate in candidates.DistinctBy(c => c.Id))
            if (byId.TryGetValue(candidate.Id, out var row) && row.CanonicalRecordingId is { } recording &&
                (candidate.MoodVector != null || candidate.OtherFeatures != null || candidate.TopGenre != null || candidate.TopMood != null))
                await tags.CaptureAudioMuseAsync(recording, candidate.MoodVector, candidate.OtherFeatures, candidate.TopGenre, candidate.TopMood, token);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    internal static LibraryTrackRecord[] Select(IEnumerable<LibraryTrackRecord> rows, LibraryTrackRecord? seed, int n, bool artistCap = true)
    {
        var selected = new List<LibraryTrackRecord>();
        var seen = seed == null ? new List<LibraryTrackRecord>() : [seed];
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (seen.Any(s => s.BackendItemId == row.BackendItemId ||
                s.CanonicalRecordingId.HasValue && s.CanonicalRecordingId == row.CanonicalRecordingId ||
                !string.IsNullOrWhiteSpace(s.MusicBrainzRecordingId) && string.Equals(s.MusicBrainzRecordingId, row.MusicBrainzRecordingId, StringComparison.OrdinalIgnoreCase))) continue;
            seen.Add(row);
            var artist = row.Artist.Trim();
            if (artistCap && artists.GetValueOrDefault(artist) >= 3) continue;
            artists[artist] = artists.GetValueOrDefault(artist) + 1;
            selected.Add(row);
            if (selected.Count == n) break;
        }
        return selected.ToArray();
    }

    internal static double? RecipeScore(string mood, AudioMuseScores scores) => mood switch
    {
        "calm" when scores is { Energy: >= 0 and <= 1, Relaxed: not null } => scores.Relaxed - .25 * scores.Energy,
        "workout" when scores is { Energy: >= 0 and <= 1, Danceable: not null, Party: not null } =>
            (scores.Danceable + scores.Party) / 2 + .25 * scores.Energy,
        _ => null
    };

    private async Task<AudioMusePreview> PublishAsync(ProtocolExecutionContext context, LibraryTrackRecord[] rows, int eligible, string? recipe, CancellationToken token)
    {
        if (rows.Length == 0) return new("too_little_analyzed_music", [], eligible, RecipeVersion: recipe);
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new StoredPreview(rows.Select(r => r.BackendItemId).ToArray(), recipe));
        await using var db = await factory.CreateDbContextAsync(token);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = id,
            OwnerUserId = context.Principal!.UserId,
            Protocol = "jellyfin",
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = PreviewKind,
            ExternalIdHash = Hash(id.ToString("N")),
            SnapshotVersion = 1,
            ProviderRevision = recipe ?? "similar-v1",
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            CorrelationId = context.CorrelationId,
            RetrievedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(token);
        return new(null, rows.Select(Song).ToArray(), eligible, id, recipe);
    }

    private static async Task<AudioMusePreview> PreviewAsync(Func<CancellationToken, Task<AudioMusePreview>> action, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await action(deadline.Token); }
        catch (Exception error) when (Unavailable(error, token) is not null) { return new(Unavailable(error, token), []); }
    }

    private static string? Unavailable(Exception error, CancellationToken token) => error switch
    {
        AudioMuseUnavailable unavailable => unavailable.Reason,
        HttpRequestException => "not_reachable",
        OperationCanceledException when !token.IsCancellationRequested => "not_reachable",
        JsonException or InvalidOperationException => "not_set_up",
        _ => null
    };
    private static void CheckLimit(int n) { if (n is < 1 or > 50) throw new ArgumentException("Choose 1–50 songs."); }
    private static AudioMuseSong Song(LibraryTrackRecord row) => new(row.BackendItemId, row.Title, row.Artist, row.Album);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record StoredPreview(string[] Ids, string? Recipe, string? PlaylistId = null);
}
