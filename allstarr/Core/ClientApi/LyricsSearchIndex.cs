using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.ClientApi;

public sealed record IndexedLyricsDocument(long Id, Guid Owner, string Backend, string ItemId, Guid? RecordingId,
    string Source, string Format, Song Song, string Provenance = "public", bool PublicCatalog = false, bool SharedSource = false, long UpdatedAt = 0);

public sealed class LyricsSearchIndex(StorageOptions options, IProviderRouter? router = null, IProviderRegistry? registry = null, ILogger<LyricsSearchIndex>? logger = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, IndexedLyricsDocument> _documents = [];
    private readonly Dictionary<Guid, long> _libraryStamps = [];
    private long _revision;
    private long Revision => Interlocked.Read(ref _revision);
    internal MemoryCache ListenerCache { get; } = new(new MemoryCacheOptions { SizeLimit = 500_000 });
    // Every cached view is keyed on the index revision or the library, settings and permission versions, so it can live
    // long enough to cover a search session without going stale.
    internal static readonly TimeSpan ListenerViewLifetime = TimeSpan.FromMinutes(1);
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    public string DatabasePath => Path.Combine(options.DataDirectory, "lyrics-search-v1.db");

    public async Task StoreAsync(ProtocolExecutionContext context, Song song, Guid? recordingId, RichLyrics rich,
        bool sharedLibrary = false, string? expectedProvenance = null)
    {
        if (context.Principal == null || rich.Lines.Count == 0 || rich.Lines.Count > 2000) return;
        var itemId = ItemKey(song);
        if (string.IsNullOrWhiteSpace(itemId)) return;
        recordingId ??= song.CanonicalRecordingId;
        var owner = sharedLibrary && song.IsLocal && rich.Source.Id == "backend" ? Guid.Empty : context.Principal.UserId;
        var providerId = LyricsVersions.ProviderId(rich.Source.Id);
        var provenance = "public";
        var sharedSource = rich.Source.Id is not ("backend" or "kept");
        if (router != null && rich.Source.Id is not ("backend" or "kept"))
        {
            var plan = await router.PlanAsync<IProviderLyricsCapability>(SourceRequest(context, providerId));
            if (plan.Candidates.FirstOrDefault() is not { } candidate) return;
            provenance = AccountProvenance(candidate.Context.Account);
            if (expectedProvenance != null && expectedProvenance != provenance) return;
            if (expectedProvenance == null && (registry?.TryGet(providerId, out var sourceDescriptor) != true ||
                !sourceDescriptor!.Capabilities.Any(capability => capability.Capability == ProviderCapabilityKind.Lyrics && capability.AccountRequirement == ProviderAccountRequirement.None))) return;
            sharedSource = providerId != "spicylyrics" && (candidate.Context.Account == null || candidate.Context.Account.Scope == ProviderAccountScope.Shared);
        }
        var publicCatalog = !song.IsLocal && song.ExternalProvider is { } provider && registry?.TryGet(provider, out var descriptor) == true &&
            descriptor!.Capabilities.Any(capability => capability.Capability == ProviderCapabilityKind.Metadata && capability.AccountRequirement == ProviderAccountRequirement.None);
        var songJson = JsonSerializer.Serialize(song, ClientApiContract.JsonOptions);
        var richJson = JsonSerializer.Serialize(rich, ClientApiContract.JsonOptions);
        if (richJson.Length > 512 * 1024) return;
        var signature = CanonicalCatalogKeys.Hash(songJson + "\n" + richJson + "\n" + recordingId + "\n" + provenance + "\n" + publicCatalog + "\n" + sharedSource);
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            var db = Open();
            using var existing = Command("SELECT id, signature FROM documents WHERE owner=$owner AND backend=$backend AND item=$item AND source=$source",
                ("$owner", owner.ToString("N")), ("$backend", context.BackendInstanceId), ("$item", itemId), ("$source", rich.Source.Id));
            long id = 0;
            using (var reader = existing.ExecuteReader())
                if (reader.Read()) { id = reader.GetInt64(0); if (reader.GetString(1) == signature) return; }
            if (id == 0 && _documents.Count >= 100_000) return;
            using var transaction = db.BeginTransaction();
            _transaction = transaction;
            if (id != 0) Execute("DELETE FROM lines WHERE doc=$id", ("$id", id));
            var updatedAt = DateTimeOffset.UtcNow.UtcTicks;
            using var write = Command("""
                INSERT INTO documents(owner,backend,item,recording,source,format,song,lyrics,signature,provenance,public_catalog,shared_source,updated_at)
                VALUES($owner,$backend,$item,$recording,$source,$format,$song,$lyrics,$signature,$provenance,$public,$shared,$updated)
                ON CONFLICT(owner,backend,item,source) DO UPDATE SET recording=excluded.recording,format=excluded.format,
                    song=excluded.song,lyrics=excluded.lyrics,signature=excluded.signature,provenance=excluded.provenance,public_catalog=excluded.public_catalog,shared_source=excluded.shared_source,updated_at=excluded.updated_at RETURNING id
                """, ("$owner", owner.ToString("N")), ("$backend", context.BackendInstanceId), ("$item", itemId),
                ("$recording", recordingId?.ToString("N")), ("$source", rich.Source.Id), ("$format", rich.Format),
                ("$song", songJson), ("$lyrics", Compress(richJson)), ("$signature", signature), ("$provenance", provenance), ("$public", publicCatalog), ("$shared", sharedSource), ("$updated", updatedAt));
            id = (long)write.ExecuteScalar()!;
            using var insert = Command("INSERT INTO lines(text,doc,ordinal) VALUES($text,$doc,$ordinal)",
                ("$text", ""), ("$doc", id), ("$ordinal", 0));
            var normalized = rich.Lines.Select(line => LyricsSearchMatcher.Normalize(line.Text)).ToArray();
            for (var index = 0; index < normalized.Length; index++)
            {
                insert.Parameters["$text"].Value = normalized[index] + "\n" + (index + 1 < normalized.Length ? normalized[index + 1] : "");
                insert.Parameters["$ordinal"].Value = index;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            _transaction = null;
            _documents[id] = new(id, owner, context.BackendInstanceId, itemId, recordingId, rich.Source.Id, rich.Format,
                JsonSerializer.Deserialize<Song>(songJson, ClientApiContract.JsonOptions)!, provenance, publicCatalog, sharedSource, updatedAt);
            Interlocked.Increment(ref _revision);
        }
        finally { _transaction = null; _gate.Release(); }
    }

    public async Task<IReadOnlyList<IndexedLyricsDocument>> SnapshotAsync(ProtocolExecutionContext context)
    {
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            Open();
            var key = (nameof(SnapshotAsync), context.BackendInstanceId, context.Principal?.UserId, Revision);
            if (ListenerCache.Get<IndexedLyricsDocument[]>(key) is { } cached) return cached;
            var documents = _documents.Values.Where(document => document.Backend == context.BackendInstanceId &&
                (document.Owner == Guid.Empty || document.Owner == context.Principal?.UserId ||
                    document.SharedSource && !document.Source.Contains(':') && document.Source is not ("backend" or "kept") && (document.Song.IsLocal || document.PublicCatalog))).ToArray();
            ListenerCache.Set(key, documents, new MemoryCacheEntryOptions
            {
                Size = Math.Max(1, documents.Length),
                AbsoluteExpirationRelativeToNow = ListenerViewLifetime
            });
            return documents;
        }
        finally { _transaction = null; _gate.Release(); }
    }

    internal async Task<RichLyrics?> ReadBackendAsync(ProtocolExecutionContext context, Song song)
    {
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            Open();
            var document = _documents.Values.Where(item => item.Backend == context.BackendInstanceId && item.ItemId == ItemKey(song) &&
                item.Source == "backend" && (item.Owner == Guid.Empty || item.Owner == context.Principal?.UserId))
                .OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (document == null) return null;
            using var read = Command("SELECT lyrics FROM documents WHERE id=$id", ("$id", document.Id));
            using var payload = new MemoryStream((byte[])read.ExecuteScalar()!);
            using var decompressed = new BrotliStream(payload, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<RichLyrics>(decompressed, ClientApiContract.JsonOptions);
        }
        finally { _gate.Release(); }
    }

    public async Task<LyricsSearchResponse> SearchAsync(ProtocolExecutionContext context, string query, int limit,
        IReadOnlyList<IndexedLyricsDocument> visible, EffectiveProviderPolicySnapshot policy,
        IReadOnlyDictionary<Guid, string> choices)
    {
        var stage = Stopwatch.StartNew();
        void Timed(string name)
        {
            logger?.LogDebug("Lyrics index stage {Stage}: {ElapsedMs:F2} ms", name, stage.Elapsed.TotalMilliseconds);
            stage.Restart();
        }
        var normalized = LyricsSearchMatcher.Normalize(query);
        if (normalized.Length == 0) return new([], false);
        var order = policy.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics,
            visible.Select(document => LyricsVersions.ProviderId(document.Source)).Distinct(StringComparer.Ordinal));
        var automatic = order.Where(policy.IsAutomatic).ToArray();
        var enabledSources = order.ToHashSet(StringComparer.Ordinal);
        var automaticSources = automatic.ToHashSet(StringComparer.Ordinal);
        var selected = visible.GroupBy(document => document.ItemId, StringComparer.Ordinal).Select(group =>
        {
            if (group.Count() == 1)
            {
                var single = group.First();
                var singlePin = single.RecordingId is { } recording ? choices.GetValueOrDefault(recording) : null;
                return enabledSources.Contains(LyricsVersions.ProviderId(single.Source)) &&
                    (single.Source == singlePin || singlePin != null && single.Source == LyricsVersions.ProviderId(singlePin) ||
                        !single.Source.Contains(':') && automaticSources.Contains(single.Source)) &&
                    (policy.Preferences.ExplicitFilter != "CleanOnly" || CleanRecordingPolicy.IsVerifiedClean(single.Song) && single.Source is "backend" or "kept")
                    ? single : null;
            }
            var candidates = group.Where(document => enabledSources.Contains(LyricsVersions.ProviderId(document.Source)) &&
                (policy.Preferences.ExplicitFilter != "CleanOnly" || CleanRecordingPolicy.IsVerifiedClean(document.Song) && document.Source is "backend" or "kept"))
                .GroupBy(document => (document.Source, document.Provenance))
                .Select(source => source.OrderByDescending(document => document.UpdatedAt)
                    .ThenByDescending(document => document.Owner == context.Principal?.UserId).First()).ToArray();
            var pin = group.Select(document => document.RecordingId is { } recording ? choices.GetValueOrDefault(recording) : null).FirstOrDefault(source => source != null);
            var chosen = candidates.Where(document => document.Source == pin).OrderByDescending(document => document.Owner == context.Principal?.UserId).FirstOrDefault() ??
                candidates.Where(document => pin != null && document.Source == LyricsVersions.ProviderId(pin))
                    .OrderByDescending(document => document.Owner == context.Principal?.UserId).FirstOrDefault() ??
                candidates.Where(document => !document.Source.Contains(':') && automatic.Contains(document.Source, StringComparer.Ordinal))
                    .OrderByDescending(document => LyricsQuality.Rank(document.Format)).ThenBy(document => Array.IndexOf(automatic, document.Source))
                    .ThenByDescending(document => document.Owner == context.Principal?.UserId).FirstOrDefault();
            var recordingId = group.Select(document => document.RecordingId).FirstOrDefault(recording => recording.HasValue);
            if (chosen == null) return null;
            return chosen.RecordingId == null && recordingId.HasValue ? chosen with { RecordingId = recordingId } : chosen;
        }).OfType<IndexedLyricsDocument>()
            .GroupBy(document => document.RecordingId?.ToString("N") ?? document.ItemId, StringComparer.Ordinal)
            .Select(group => group.Count() == 1 ? group.First() : group.OrderByDescending(document => document.Song.IsLocal)
                .ThenByDescending(document => document.Song.ExternalProvider == "allstarr").ThenBy(document => document.ItemId, StringComparer.Ordinal).First())
            .ToDictionary(document => document.Id);
        Timed("selection");
        if (selected.Count == 0) return new([], false);
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            Open();
            Timed("lock-and-open");
            var scorer = new LyricsSearchMatcher.PreparedQuery(normalized);
            _connection!.CreateFunction<string, double>("lyrics_score", text =>
            {
                var split = text.IndexOf('\n');
                return scorer.Score(split < 0 ? text.AsSpan() : text.AsSpan(0, split),
                    split < 0 ? default : text.AsSpan(split + 1));
            }, isDeterministic: true);
            using (var transaction = _connection!.BeginTransaction())
            {
                _transaction = transaction;
                Execute("DELETE FROM eligible");
                using var insert = Command("INSERT INTO eligible(id) VALUES($id)", ("$id", 0L));
                foreach (var document in selected.Values)
                {
                    if (!_documents.TryGetValue(document.Id, out var current) || current.UpdatedAt != document.UpdatedAt) continue;
                    insert.Parameters["$id"].Value = document.Id;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
                _transaction = null;
            }
            Timed("eligible");
            var fts = string.Join(" OR ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Select(word => "\"" + word + "\"*"));
            var matches = new List<(long Id, int Ordinal)>();
            // At most 2,000 lines per document still allows limit + 1 distinct matches.
            using (var exact = Command("""
                SELECT doc, ordinal FROM lines JOIN eligible ON eligible.id=lines.doc
                WHERE lines MATCH $phrase AND lyrics_score(text)=1 ORDER BY lines.rowid LIMIT $take
                """, ("$phrase", "\"" + normalized + "\""), ("$take", (limit + 1) * 2000)))
            using (var reader = exact.ExecuteReader())
            {
                var seen = new HashSet<long>();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (seen.Add(id)) matches.Add((id, reader.GetInt32(1)));
                    if (matches.Count > limit) break;
                }
            }
            if (matches.Count <= limit)
            {
                matches.Clear();
                using var search = Command("""
                    SELECT doc, ordinal, max(lyrics_score(text)) AS score
                    FROM lines JOIN eligible ON eligible.id=lines.doc
                    WHERE lines MATCH $fts GROUP BY doc HAVING score>0
                    ORDER BY score DESC, doc LIMIT $take
                    """, ("$fts", fts), ("$take", limit + 1));
                using var reader = search.ExecuteReader();
                while (reader.Read()) matches.Add((reader.GetInt64(0), reader.GetInt32(1)));
            }
            Timed("fts");
            var hydrated = new Dictionary<long, RichLyrics>();
            RichLyrics Read(long id)
            {
                if (hydrated.TryGetValue(id, out var cached)) return cached;
                using var read = Command("SELECT lyrics FROM documents WHERE id=$id", ("$id", id));
                using var payload = new MemoryStream((byte[])read.ExecuteScalar()!);
                using var decompressed = new BrotliStream(payload, CompressionMode.Decompress);
                return hydrated[id] = JsonSerializer.Deserialize<RichLyrics>(decompressed, ClientApiContract.JsonOptions)!;
            }
            var results = new List<LyricsSearchResult>();
            foreach (var match in matches.Take(limit))
            {
                var document = selected[match.Id];
                var rich = Read(match.Id);
                var index = match.Ordinal;
                var line = rich.Lines[index];
                var timing = line;
                if (line.StartMs == null)
                {
                    var text = LyricsSearchMatcher.Normalize(line.Text);
                    timing = visible.Where(candidate => candidate.Source != document.Source && candidate.Format is "line" or "word" &&
                            enabledSources.Contains(LyricsVersions.ProviderId(candidate.Source)) &&
                            (candidate.ItemId == document.ItemId || document.RecordingId.HasValue && candidate.RecordingId == document.RecordingId) &&
                            (policy.Preferences.ExplicitFilter != "CleanOnly" || CleanRecordingPolicy.IsVerifiedClean(candidate.Song) && candidate.Source is "backend" or "kept") &&
                            _documents.TryGetValue(candidate.Id, out var current) && current.UpdatedAt == candidate.UpdatedAt)
                        .SelectMany(candidate => Read(candidate.Id).Lines.Select((value, ordinal) => new { Line = value, Ordinal = ordinal }))
                        .Where(candidate => candidate.Line.StartMs.HasValue && LyricsSearchMatcher.Normalize(candidate.Line.Text) == text)
                        .OrderBy(candidate => Math.Abs(candidate.Ordinal - index)).Select(candidate => candidate.Line).FirstOrDefault() ?? line;
                }
                var before = index > 0 ? rich.Lines[index - 1].Text : null;
                var after = index + 1 < rich.Lines.Count ? rich.Lines[index + 1].Text : null;
                var found = LyricsSearchMatcher.Match(line.Text, after, query);
                var song = document.Song;
                var albumId = song.AlbumId;
                if (!song.IsLocal && !string.IsNullOrWhiteSpace(albumId) && !albumId.StartsWith("ext-", StringComparison.Ordinal))
                    albumId = $"ext-{song.ExternalProvider}-album-{albumId}";
                results.Add(new(document.ItemId, song.Title, song.Artist, song.Album, albumId, !song.IsLocal,
                    new(line.Text, timing.StartMs, timing.EndMs, before, after), found.Highlights, found.Score, rich.Source));
            }
            Timed("hydrate");
            return new(results, matches.Count > limit);
        }
        finally { _transaction = null; _gate.Release(); }
    }

    public async Task ReconcileVersionsAsync(ProtocolExecutionContext context, Song song, string provider,
        IReadOnlySet<string> versions, bool unavailable = false)
    {
        var snapshot = await SnapshotAsync(context);
        var retained = snapshot.Where(document => document.ItemId == ItemKey(song) &&
            (LyricsVersions.ProviderId(document.Source) != provider || versions.Contains(document.Source) || !unavailable && document.Source == provider))
            .Select(document => document.Source).ToHashSet(StringComparer.Ordinal);
        await ReconcileStoredAsync(context, song, retained, new HashSet<string>(StringComparer.Ordinal) { provider });
    }

    public async Task ReconcileStoredAsync(ProtocolExecutionContext context, Song song, IReadOnlySet<string> sources, IReadOnlySet<string>? unavailableSources = null)
    {
        var itemId = song.IsLocal ? song.Id : $"ext-{song.ExternalProvider}-song-{song.ExternalId}";
        if (song.IsLocal && Guid.TryParse(itemId, out var nativeId)) itemId = nativeId.ToString("N");
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            Open();
            var ids = _documents.Values.Where(document => document.Owner == context.Principal?.UserId &&
                document.Backend == context.BackendInstanceId && document.ItemId == itemId && !sources.Contains(document.Source) &&
                (LyricsVersions.ProviderId(document.Source) is "kept" or "lrclib" or "lrcred" or "amll" or "neteaselyrics" or "qqlyrics" or "kugoulyrics" or "lrcmux" or "unison" or "genius" or "paxsenix" or "lyricsplus" or "musixmatch" or "spicylyrics" or "spotify") &&
                (unavailableSources == null || document.Source == "kept" || unavailableSources.Contains(LyricsVersions.ProviderId(document.Source))))
                .Select(document => document.Id).ToArray();
            using var transaction = _connection!.BeginTransaction();
            _transaction = transaction;
            foreach (var id in ids)
            {
                Execute("DELETE FROM lines WHERE doc=$id", ("$id", id));
                Execute("DELETE FROM documents WHERE id=$id", ("$id", id));
                _documents.Remove(id);
            }
            transaction.Commit();
            if (ids.Length > 0) Interlocked.Increment(ref _revision);
        }
        finally { _transaction = null; _gate.Release(); }
    }

    public async Task RemoveLibrarySourceAsync(string backend, string itemId, CancellationToken token)
    {
        if (Guid.TryParse(itemId, out var nativeId)) itemId = nativeId.ToString("N");
        await _gate.WaitAsync(token);
        try
        {
            Open();
            var ids = _documents.Values.Where(document => document.Backend == backend && document.ItemId == itemId && document.Source == "backend")
                .Select(document => document.Id).ToArray();
            using var transaction = _connection!.BeginTransaction();
            _transaction = transaction;
            foreach (var id in ids)
            {
                Execute("DELETE FROM lines WHERE doc=$id", ("$id", id));
                Execute("DELETE FROM documents WHERE id=$id", ("$id", id));
                _documents.Remove(id);
            }
            transaction.Commit();
            if (ids.Length > 0) Interlocked.Increment(ref _revision);
            _transaction = null;
        }
        finally { _transaction = null; _gate.Release(); }
    }

    public async Task<bool> LibraryIsCurrentAsync(Guid id, long revision, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Open();
            return _libraryStamps.TryGetValue(id, out var current) && current == revision;
        }
        finally { _transaction = null; _gate.Release(); }
    }

    public async Task MarkLibraryAsync(Guid id, long revision, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Open();
            Execute("INSERT INTO library_state(id,revision) VALUES($id,$revision) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision",
                ("$id", id.ToString("N")), ("$revision", revision));
            _libraryStamps[id] = revision;
        }
        finally { _transaction = null; _gate.Release(); }
    }

    internal static string? ItemKey(Song song)
    {
        var id = song.IsLocal ? song.Id : string.IsNullOrWhiteSpace(song.ExternalProvider) || string.IsNullOrWhiteSpace(song.ExternalId)
            ? null : $"ext-{song.ExternalProvider}-song-{song.ExternalId}";
        return song.IsLocal && Guid.TryParse(id, out var native) ? native.ToString("N") : id;
    }

    internal static string AccountProvenance(ProviderAccountContext? account) => AccountProvenance(account?.AccountId, account?.Revision);
    internal static string AccountProvenance(Guid? account, long? revision) => account == null ? "public" : $"{account:N}:{revision}";

    internal static ProviderRouteRequest SourceRequest(ProtocolExecutionContext context, string source) => SourceRequestCore(context, LyricsVersions.ProviderId(source));

    private static ProviderRouteRequest SourceRequestCore(ProtocolExecutionContext context, string source) => new(ProviderCapabilityKind.Lyrics,
        context.RequireActor(), new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, false, true, false, [source]), "lyrics-search-sources", context.CorrelationId,
        context.Deadline, [source], cancellationToken: context.CancellationToken, purpose: ProviderRoutePurpose.ManualSelection);

    private static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compressed.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private SqliteConnection Open()
    {
        if (_connection != null) return _connection;
        Directory.CreateDirectory(options.DataDirectory);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        db.Open();
        _connection = db;
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8192;
            PRAGMA max_page_count=262144;
            CREATE TABLE IF NOT EXISTS documents(id INTEGER PRIMARY KEY,owner TEXT NOT NULL,backend TEXT NOT NULL,item TEXT NOT NULL,
                recording TEXT,source TEXT NOT NULL,format TEXT NOT NULL,song TEXT NOT NULL,lyrics BLOB NOT NULL,signature TEXT NOT NULL,provenance TEXT NOT NULL,public_catalog INTEGER NOT NULL,shared_source INTEGER NOT NULL,updated_at INTEGER NOT NULL,
                UNIQUE(owner,backend,item,source));
            CREATE VIRTUAL TABLE IF NOT EXISTS lines USING fts5(text,doc UNINDEXED,ordinal UNINDEXED,prefix='2 3');
            CREATE TABLE IF NOT EXISTS library_state(id TEXT PRIMARY KEY,revision INTEGER NOT NULL);
            CREATE TEMP TABLE eligible(id INTEGER PRIMARY KEY);
            """);
        using (var checkpoints = Command("SELECT id,revision FROM library_state"))
        using (var checkpointReader = checkpoints.ExecuteReader())
            while (checkpointReader.Read()) _libraryStamps[Guid.Parse(checkpointReader.GetString(0))] = checkpointReader.GetInt64(1);
        using var command = Command("SELECT id,owner,backend,item,recording,source,format,song,provenance,public_catalog,shared_source,updated_at FROM documents");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            _documents[id] = new(id, Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
                JsonSerializer.Deserialize<Song>(reader.GetString(7), ClientApiContract.JsonOptions)!, reader.GetString(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.GetInt64(11));
        }
        return db;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    public void Dispose() { ListenerCache.Dispose(); _connection?.Dispose(); _gate.Dispose(); }
}
