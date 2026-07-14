using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Playlists.Targets;

namespace allstarr.Tests;

public sealed class BackendPlaylistTargetTests
{
    [Fact]
    public void Durable_context_contains_only_stable_references_and_request_deduplicates_tracks()
    {
        var context = new BackendPlaylistTargetContext("backend-a", "principal-a", "secret-ref-a");
        var json = JsonSerializer.Serialize(context);

        Assert.Contains("secret-ref-a", json);
        Assert.DoesNotContain("Header", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Query", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Token", json, StringComparison.OrdinalIgnoreCase);

        var request = new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile,
            new BackendPlaylistMetadata("Mix"),
            ["one", "two", "one", "two"],
            "run-1",
            "playlist-1");
        Assert.Equal(["one", "two"], request.OrderedBackendItemIds);
    }

    [Fact]
    public async Task Jellyfin_reconcile_removes_adds_reorders_metadata_and_is_duplicate_safe()
    {
        var backend = new JellyfinFakeBackend("p1", "Old", ["b", "manual", "a", "stale"]);
        var target = new JellyfinPlaylistTarget(
            new HttpClient(backend),
            new Uri("https://jellyfin.test/"),
            new FakeAuthenticationResolver(headers: new Dictionary<string, string> { ["X-Emby-Token"] = "ephemeral" }));
        var context = Context();
        var before = (await target.ReadAsync(context, "p1", default)).Value!;
        var request = new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile,
            new BackendPlaylistMetadata("Road Mix", "Local matches", [1, 2, 3], "image/png"),
            ["a", "b", "c", "a"],
            "sync-1",
            "p1",
            expectedFingerprint: before.Fingerprint,
            syncOwnedBackendItemIds: ["a", "b", "stale"],
            removeStaleSyncOwnedItems: true);

        var result = await target.WriteAsync(context, request, default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Changed);
        Assert.Equal(["a", "b", "c", "manual"], result.Value.Snapshot.Members.Select(member => member.BackendItemId));
        Assert.Equal("Road Mix", result.Value.Snapshot.Name);
        Assert.Equal("Local matches", result.Value.Snapshot.Description);
        Assert.True(backend.ArtworkWritten);
        Assert.All(backend.Requests, requestRecord => Assert.Equal("ephemeral", requestRecord.AuthToken));
        Assert.DoesNotContain(backend.Requests, requestRecord => requestRecord.Path.Contains("Audio", StringComparison.OrdinalIgnoreCase));

        var mutations = backend.MutationCount;
        var retryMetadata = request.Metadata with { Artwork = null, ArtworkContentType = null };
        var retry = await target.WriteAsync(context, new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile,
            retryMetadata,
            request.OrderedBackendItemIds,
            "sync-1",
            "p1",
            expectedFingerprint: result.Value.Snapshot.Fingerprint,
            syncOwnedBackendItemIds: ["a", "b", "c"],
            removeStaleSyncOwnedItems: true), default);
        Assert.True(retry.IsSuccess);
        Assert.False(retry.Value!.Changed);
        Assert.Equal(mutations, backend.MutationCount);
    }

    [Fact]
    public async Task Jellyfin_conflict_does_not_mutate_and_recreate_returns_staged_recovery_id()
    {
        var backend = new JellyfinFakeBackend("p1", "Mix", ["a"]);
        var target = new JellyfinPlaylistTarget(new HttpClient(backend), new Uri("https://jellyfin.test/"));
        var conflict = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile,
            new BackendPlaylistMetadata("Mix"), ["b"], "sync-2", "p1",
            expectedFingerprint: "stale"), default);
        Assert.Equal(BackendPlaylistTargetStatus.Conflict, conflict.Status);
        Assert.Equal(0, backend.MutationCount);

        var recreated = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Recreate,
            new BackendPlaylistMetadata("Mix"), ["b", "b", "a"], "sync-3", "p1"), default);
        Assert.True(recreated.IsSuccess);
        Assert.True(recreated.Value!.ReplacementRequiresCleanup);
        Assert.Equal("p1", recreated.Value.ReplacedPlaylistId);
        Assert.Equal(recreated.Value.Snapshot.BackendPlaylistId, recreated.RecoveryPlaylistId);
        Assert.Equal(["b", "a"], recreated.Value.Snapshot.Members.Select(member => member.BackendItemId));
        Assert.Equal(["a"], backend.Playlists["p1"].Members);
    }

    [Fact]
    public async Task Jellyfin_preserves_upstream_auth_failure_and_cancellation()
    {
        var unauthorized = new JellyfinFakeBackend("p1", "Mix", []) { ForcedStatus = HttpStatusCode.Forbidden };
        var target = new JellyfinPlaylistTarget(new HttpClient(unauthorized), new Uri("https://jellyfin.test/"));
        var result = await target.ReadAsync(Context(), "p1", default);
        Assert.Equal(BackendPlaylistTargetStatus.Unauthorized, result.Status);
        Assert.Equal(HttpStatusCode.Forbidden, result.UpstreamStatus);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, new BackendPlaylistMetadata("Mix"), [], "sync-4", "p1"), cancellation.Token);
        Assert.Equal(BackendPlaylistTargetStatus.Cancelled, cancelled.Status);
        Assert.Equal("p1", cancelled.RecoveryPlaylistId);
    }

    [Fact]
    public async Task Jellyfin_recreate_can_resume_its_staged_target_without_creating_a_duplicate()
    {
        var backend = new JellyfinFakeBackend("p1", "Mix", ["a"]) { FailNextMetadata = true };
        var target = new JellyfinPlaylistTarget(new HttpClient(backend), new Uri("https://jellyfin.test/"));
        var request = new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Recreate, new BackendPlaylistMetadata("Fresh"),
            ["b", "a"], "same-run", "p1");

        var interrupted = await target.WriteAsync(Context(), request, default);
        Assert.Equal(BackendPlaylistTargetStatus.BackendFailure, interrupted.Status);
        Assert.NotNull(interrupted.RecoveryPlaylistId);
        var playlistCount = backend.Playlists.Count;

        var resumed = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Recreate, request.Metadata, request.OrderedBackendItemIds,
            request.IdempotencyKey, request.BackendPlaylistId,
            recoveryPlaylistId: interrupted.RecoveryPlaylistId), default);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(playlistCount, backend.Playlists.Count);
        Assert.Equal(interrupted.RecoveryPlaylistId, resumed.Value!.Snapshot.BackendPlaylistId);
    }

    [Fact]
    public async Task Subsonic_reconcile_preserves_order_auth_and_reports_metadata_capabilities()
    {
        var backend = new SubsonicFakeBackend("p1", "Mix", ["b", "manual", "a", "stale"]);
        var target = new SubsonicPlaylistTarget(
            new HttpClient(backend),
            new Uri("https://navidrome.test/"),
            new FakeAuthenticationResolver(form: [Pair("u", "alice"), Pair("t", "hash"), Pair("s", "salt"), Pair("v", "1.16.1"), Pair("c", "allstarr")]));
        var context = Context();
        var before = (await target.ReadAsync(context, "p1", default)).Value!;
        var request = new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile,
            new BackendPlaylistMetadata("Mix", "not-supported", [4, 5], "image/jpeg"),
            ["a", "b", "c", "a"], "sub-sync-1", "p1", expectedRevision: before.NativeRevision,
            syncOwnedBackendItemIds: ["a", "b", "stale"], removeStaleSyncOwnedItems: true);

        var result = await target.WriteAsync(context, request, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b", "c", "manual"], result.Value!.Snapshot.Members.Select(member => member.BackendItemId));
        Assert.Equal(["description", "artwork"], result.Value.UnsupportedMetadataFields);
        Assert.All(backend.Requests, requestRecord =>
        {
            Assert.Equal("alice", requestRecord.Parameters["u"].Single());
            Assert.Equal("hash", requestRecord.Parameters["t"].Single());
            Assert.Equal("json", requestRecord.Parameters["f"].Single());
        });
        Assert.DoesNotContain(backend.Requests, requestRecord => requestRecord.Path.Contains("stream", StringComparison.OrdinalIgnoreCase));

        var mutations = backend.MutationCount;
        var retry = await target.WriteAsync(context, new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, request.Metadata, request.OrderedBackendItemIds,
            "sub-sync-1", "p1", expectedRevision: result.Value.Snapshot.NativeRevision,
            syncOwnedBackendItemIds: ["a", "b", "c"], removeStaleSyncOwnedItems: true), default);
        Assert.True(retry.IsSuccess);
        Assert.False(retry.Value!.Changed);
        Assert.Equal(mutations, backend.MutationCount);
    }

    [Fact]
    public async Task Subsonic_recreate_is_staged_duplicate_safe_and_protocol_failure_is_preserved()
    {
        var backend = new SubsonicFakeBackend("p1", "Mix", ["a"]);
        var target = new SubsonicPlaylistTarget(new HttpClient(backend), new Uri("https://subsonic.test/"));
        var recreated = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Recreate, new BackendPlaylistMetadata("Fresh"),
            ["b", "b", "a"], "sub-sync-2", "p1"), default);
        Assert.True(recreated.IsSuccess);
        Assert.True(recreated.Value!.ReplacementRequiresCleanup);
        Assert.Equal(["b", "a"], recreated.Value.Snapshot.Members.Select(member => member.BackendItemId));
        Assert.Equal(["a"], backend.Playlists["p1"].Members);

        backend.ProtocolFailureCode = 40;
        var failed = await target.FindByNameAsync(Context(), "Fresh", default);
        Assert.Equal(BackendPlaylistTargetStatus.BackendFailure, failed.Status);
        Assert.Equal(HttpStatusCode.OK, failed.UpstreamStatus);
        Assert.Equal("subsonic-40", failed.ErrorCode);

        backend.ProtocolFailureCode = null;
        backend.ForcedStatus = HttpStatusCode.Unauthorized;
        var unauthorized = await target.ReadAsync(Context(), "p1", default);
        Assert.Equal(BackendPlaylistTargetStatus.Unauthorized, unauthorized.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.UpstreamStatus);
    }

    [Fact]
    public async Task Subsonic_revision_conflict_and_cancellation_never_mutate()
    {
        var backend = new SubsonicFakeBackend("p1", "Mix", ["a"]);
        var target = new SubsonicPlaylistTarget(new HttpClient(backend), new Uri("https://subsonic.test/"));
        var conflict = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, new BackendPlaylistMetadata("Mix"),
            ["b"], "sub-sync-conflict", "p1", expectedRevision: "old"), default);
        Assert.Equal(BackendPlaylistTargetStatus.Conflict, conflict.Status);
        Assert.Equal(0, backend.MutationCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, new BackendPlaylistMetadata("Mix"),
            ["b"], "sub-sync-cancel", "p1"), cancellation.Token);
        Assert.Equal(BackendPlaylistTargetStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, backend.MutationCount);
    }

    private static BackendPlaylistTargetContext Context() => new("backend-1", "user-1", "credential-ref-1");
    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    private sealed class FakeAuthenticationResolver(
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<KeyValuePair<string, string>>? form = null) : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BackendPlaylistAuthentication(headers ?? new Dictionary<string, string>(), form ?? []));
    }

    private sealed record RequestRecord(string Path, string? AuthToken, IReadOnlyDictionary<string, string[]> Parameters);

    private sealed class JellyfinFakeBackend : HttpMessageHandler
    {
        public JellyfinFakeBackend(string id, string name, IReadOnlyList<string> members) =>
            Playlists[id] = new PlaylistState(name, members.ToList());

        public Dictionary<string, PlaylistState> Playlists { get; } = [];
        public List<RequestRecord> Requests { get; } = [];
        public HttpStatusCode? ForcedStatus { get; set; }
        public int MutationCount { get; private set; }
        public bool ArtworkWritten { get; private set; }
        public bool FailNextMetadata { get; set; }
        private int _nextId = 2;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var query = ParseQuery(request.RequestUri.Query);
            Requests.Add(new(path, request.Headers.TryGetValues("X-Emby-Token", out var values) ? values.Single() : null, query));
            if (ForcedStatus != null) return new(ForcedStatus.Value);

            if (request.Method == HttpMethod.Get && path.StartsWith("Users/user-1/Items", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                if (parts.Length == 4)
                {
                    var id = parts[3];
                    return Json(new { Id = id, Name = Playlists[id].Name, Overview = Playlists[id].Description, ImageTags = new { Primary = Playlists[id].Artwork ? "art" : null } });
                }
                return Json(new { Items = Playlists.Select(pair => new { Id = pair.Key, Name = pair.Value.Name }).ToArray() });
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("Playlists/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
            {
                var id = path.Split('/')[1];
                return Json(new { Items = Playlists[id].Members.Select(item => new { Id = item, PlaylistItemId = $"entry-{item}" }).ToArray() });
            }
            if (request.Method == HttpMethod.Post && path == "Playlists")
            {
                MutationCount++;
                var id = $"p{_nextId++}";
                Playlists[id] = new(query["Name"].Single(), SplitCsv(query, "Ids"));
                return Json(new { Id = id });
            }
            if (request.Method == HttpMethod.Delete && path.EndsWith("/Items", StringComparison.Ordinal))
            {
                MutationCount++;
                var state = Playlists[path.Split('/')[1]];
                var ids = SplitCsv(query, "EntryIds").Select(value => value.Replace("entry-", "", StringComparison.Ordinal)).ToHashSet();
                state.Members.RemoveAll(ids.Contains);
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/Items", StringComparison.Ordinal))
            {
                MutationCount++;
                Playlists[path.Split('/')[1]].Members.AddRange(SplitCsv(query, "Ids"));
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path.Contains("/Move/", StringComparison.Ordinal))
            {
                MutationCount++;
                var parts = path.Split('/');
                var state = Playlists[parts[1]];
                var item = parts[3].Replace("entry-", "", StringComparison.Ordinal);
                state.Members.Remove(item);
                state.Members.Insert(int.Parse(parts[5]), item);
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path.StartsWith("Items/", StringComparison.Ordinal) && path.EndsWith("/Images/Primary", StringComparison.Ordinal))
            {
                MutationCount++;
                Playlists[path.Split('/')[1]].Artwork = ArtworkWritten = true;
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path.StartsWith("Items/", StringComparison.Ordinal))
            {
                MutationCount++;
                if (FailNextMetadata)
                {
                    FailNextMetadata = false;
                    return new(HttpStatusCode.ServiceUnavailable);
                }
                var state = Playlists[path.Split('/')[1]];
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                state.Name = document.RootElement.GetProperty("name").GetString()!;
                state.Description = document.RootElement.GetProperty("overview").GetString();
                return new(HttpStatusCode.NoContent);
            }
            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        private static List<string> SplitCsv(IReadOnlyDictionary<string, string[]> query, string key) =>
            query.TryGetValue(key, out var values) ? values.Single().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
    }

    private sealed class SubsonicFakeBackend : HttpMessageHandler
    {
        public SubsonicFakeBackend(string id, string name, IReadOnlyList<string> members) =>
            Playlists[id] = new PlaylistState(name, members.ToList());

        public Dictionary<string, PlaylistState> Playlists { get; } = [];
        public List<RequestRecord> Requests { get; } = [];
        public int MutationCount { get; private set; }
        public int? ProtocolFailureCode { get; set; }
        public HttpStatusCode? ForcedStatus { get; set; }
        private int _nextId = 2;
        private int _revision = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ForcedStatus != null) return new(ForcedStatus.Value);
            var parameters = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            var endpoint = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            Requests.Add(new(endpoint, null, parameters));
            if (ProtocolFailureCode != null) return Json(new { status = "failed", error = new { code = ProtocolFailureCode, message = "denied" } });
            if (endpoint == "getPlaylists")
                return Json(new { status = "ok", playlists = new { playlist = Playlists.Select(pair => Summary(pair.Key, pair.Value)).ToArray() } });
            if (endpoint == "getPlaylist")
            {
                var id = parameters["id"].Single();
                var state = Playlists[id];
                return Json(new { status = "ok", playlist = new { id, name = state.Name, changed = $"r{state.Revision}", entry = state.Members.Select(item => new { id = item }).ToArray() } });
            }
            if (endpoint == "createPlaylist")
            {
                MutationCount++;
                var members = parameters.TryGetValue("songId", out var songs) ? songs.ToList() : [];
                if (parameters.TryGetValue("playlistId", out var ids))
                {
                    var state = Playlists[ids.Single()];
                    state.Members = members;
                    state.Revision = ++_revision;
                }
                else
                {
                    var id = $"p{_nextId++}";
                    Playlists[id] = new(parameters["name"].Single(), members) { Revision = ++_revision };
                }
                return Json(new { status = "ok" });
            }
            return new(HttpStatusCode.NotFound);
        }

        private static object Summary(string id, PlaylistState state) => new { id, name = state.Name, changed = $"r{state.Revision}" };
        private static HttpResponseMessage Json(object response) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = response }), Encoding.UTF8, "application/json")
        };
    }

    private sealed class PlaylistState(string name, List<string> members)
    {
        public string Name { get; set; } = name;
        public string? Description { get; set; }
        public bool Artwork { get; set; }
        public List<string> Members { get; set; } = members;
        public int Revision { get; set; } = 1;
    }

    private static Dictionary<string, string[]> ParseQuery(string query)
    {
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(part => new KeyValuePair<string, string>(
                Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                Uri.UnescapeDataString((part.Length == 2 ? part[1] : "").Replace('+', ' '))))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
