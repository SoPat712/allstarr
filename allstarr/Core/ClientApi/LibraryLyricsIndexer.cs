using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.ClientApi;

public sealed record LyricsSearchChange(Guid LibraryTrackId, Guid UserId = default, Guid RecordingId = default);
public sealed record LyricsSongChange(Guid UserId, string ItemId);

public sealed class LyricsSearchChanges(TimeProvider? timeProvider = null) : IApplicationCacheWriteObserver
{
    private readonly Channel<LyricsSearchChange> _changes = Channel.CreateBounded<LyricsSearchChange>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _version;
    public long Version => Interlocked.Read(ref _version);
    public void LibraryChanged(Guid id)
    {
        Interlocked.Increment(ref _version);
        _changes.Writer.TryWrite(new(id));
    }
    private readonly ConcurrentDictionary<(Guid User, Guid Recording), long> _recordings = new();
    private int _refreshAll = 1;
    public void RecordingChanged(Guid user, Guid recording)
    {
        Interlocked.Increment(ref _version);
        if (_recordings.Count < 100_000) _recordings[(user, recording)] = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks;
        else Interlocked.Exchange(ref _refreshAll, 1);
        _changes.Writer.TryWrite(new(default));
    }
    public void RetryRecording(Guid user, Guid recording)
    {
        var due = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(1).UtcTicks;
        if (_recordings.Count < 100_000) _recordings.AddOrUpdate((user, recording), due, (_, previous) => Math.Min(previous, due));
        else Interlocked.Exchange(ref _refreshAll, 1);
    }
    public IReadOnlyList<LyricsSearchChange> TakeRecordings()
    {
        var result = new List<LyricsSearchChange>();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks;
        foreach (var entry in _recordings.Where(entry => entry.Value <= now).Take(32))
            if (_recordings.TryRemove(entry.Key, out _)) result.Add(new(default, entry.Key.User, entry.Key.Recording));
        return result;
    }
    private readonly ConcurrentDictionary<(Guid User, string Item), long> _songs = new();
    private readonly object _watchGate = new();
    private readonly Dictionary<string, List<(Guid User, string Item)>> _watched = new(StringComparer.Ordinal);
    private int _watchBindings;
    public void SongChanged(Guid user, string item)
    {
        Interlocked.Increment(ref _version);
        if (_songs.Count < 100_000) _songs[(user, item)] = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks;
        else Interlocked.Exchange(ref _refreshAll, 1);
        _changes.Writer.TryWrite(new(default));
    }
    public void RetrySong(Guid user, string item)
    {
        var due = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(1).UtcTicks;
        if (_songs.Count < 100_000) _songs.AddOrUpdate((user, item), due, (_, previous) => Math.Min(previous, due));
        else Interlocked.Exchange(ref _refreshAll, 1);
    }
    public IReadOnlyList<LyricsSongChange> TakeSongs()
    {
        var result = new List<LyricsSongChange>();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks;
        foreach (var entry in _songs.Where(entry => entry.Value <= now).Take(32))
            if (_songs.TryRemove(entry.Key, out _)) result.Add(new(entry.Key.User, entry.Key.Item));
        return result;
    }
    public void WatchCacheKey(string key, Guid user, string item)
    {
        lock (_watchGate)
        {
            if (_watched.TryGetValue(key, out var targets) && targets.Contains((user, item))) return;
            if (_watchBindings >= 500_000) { Interlocked.Exchange(ref _refreshAll, 1); return; }
            if (targets == null) _watched[key] = targets = [];
            targets.Add((user, item));
            _watchBindings++;
        }
    }
    public void Written(string key)
    {
        (Guid User, string Item)[] targets;
        lock (_watchGate) targets = _watched.TryGetValue(key, out var watched) ? watched.ToArray() : [];
        foreach (var target in targets) SongChanged(target.User, target.Item);
    }
    public bool TakeRefreshAll() => Interlocked.Exchange(ref _refreshAll, 0) != 0;
    public ChannelReader<LyricsSearchChange> Reader => _changes.Reader;
}

public sealed class LibraryLyricsIndexer(IDbContextFactory<AllstarrDbContext> factory, LyricsSearchIndex index,
    LyricsSearchChanges changes, IHttpClientFactory clients, IOptions<JellyfinSettings> settings,
    IEffectiveProviderPolicyResolver policies, IApplicationCache cache, IDurableRuntimeSettings runtime,
    ITrackIdentityService identities, IKeptLyricsReader kept, IProviderRouter router, ILibraryIndexService library, IProtocolProviderGateway gateway, IConfiguration configuration, DurableStorageState storage, ILogger<LibraryLyricsIndexer> logger,
    LyricsRefreshStore? refreshStore = null) : BackgroundService
{
    public static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storage.GetSnapshot().Readiness == DurableStorageReadiness.Ready &&
                    !string.Equals(configuration["Backend:Type"], "Subsonic", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTimeOffset.UtcNow >= nextSweep)
                    {
                        await SweepAsync(stoppingToken);
                        nextSweep = DateTimeOffset.UtcNow.AddMinutes(5);
                    }
                    for (var pending = 0; pending < 128 && changes.Reader.TryRead(out var change); pending++)
                        if (change.LibraryTrackId != Guid.Empty) await SafelyAsync(() => ProcessAsync(change.LibraryTrackId, stoppingToken), stoppingToken);
                    await DrainRecordingsAsync(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Library lyrics indexing was deferred ({ErrorType})", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task DrainRecordingsAsync(CancellationToken token)
    {
        foreach (var change in changes.TakeRecordings())
            if (!await SafelyAsync(() => RefreshRecordingAsync(change.UserId, change.RecordingId, token), token))
                changes.RetryRecording(change.UserId, change.RecordingId);
        foreach (var change in changes.TakeSongs())
            if (!await SafelyAsync(() => RefreshSongAsync(change.UserId, change.ItemId, token), token))
                changes.RetrySong(change.UserId, change.ItemId);
    }

    private async Task<bool> SafelyAsync(Func<Task> action, CancellationToken token)
    {
        try { await action(); return true; }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogWarning("Library lyrics entry was deferred ({ErrorType})", exception.GetType().Name);
            return false;
        }
    }

    public async Task SweepAsync(CancellationToken token)
    {
        var refreshAll = changes.TakeRefreshAll();
        var offset = 0;
        while (true)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var page = await db.LibraryTracks.AsNoTracking().Where(track => track.Protocol == "jellyfin")
                .OrderBy(track => track.Id).Skip(offset).Take(128).ToArrayAsync(token);
            foreach (var track in page)
                if (refreshAll || !await index.LibraryIsCurrentAsync(track.Id, LibraryRevision(track), token))
                    await SafelyAsync(() => ProcessAsync(track.Id, token, storedOnly: refreshAll), token);
            for (var pending = 0; pending < 128 && changes.Reader.TryRead(out var change); pending++)
            {
                if (change.LibraryTrackId != Guid.Empty) await SafelyAsync(() => ProcessAsync(change.LibraryTrackId, token), token);
            }
            await DrainRecordingsAsync(token);
            if (page.Length < 128) break;
            offset += page.Length;
        }
        offset = 0;
        while (true)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var page = await db.ExternalMetadataSnapshots.AsNoTracking().Where(snapshot => snapshot.Protocol == "jellyfin" &&
                    snapshot.ProviderTrackIdentityId != null && (snapshot.ResourceKind == "track" || snapshot.ResourceKind == "playback-metadata"))
                .OrderBy(snapshot => snapshot.Id).Skip(offset).Take(128).Select(snapshot => new
                {
                    snapshot.Id,
                    snapshot.OwnerUserId,
                    snapshot.RetrievedAt,
                    Recording = db.ProviderTrackIdentities.Where(link => link.Id == snapshot.ProviderTrackIdentityId)
                        .Select(link => link.CanonicalRecordingId).FirstOrDefault()
                }).ToArrayAsync(token);
            foreach (var snapshot in page)
            {
                if (snapshot.Recording == Guid.Empty || !refreshAll && await index.LibraryIsCurrentAsync(snapshot.Id, snapshot.RetrievedAt.UtcTicks, token)) continue;
                if (await SafelyAsync(() => RefreshRecordingAsync(snapshot.OwnerUserId, snapshot.Recording, token), token))
                    await index.MarkLibraryAsync(snapshot.Id, snapshot.RetrievedAt.UtcTicks, token);
            }
            if (page.Length < 128) break;
            offset += page.Length;
        }
    }

    public async Task ProcessAsync(Guid id, CancellationToken token, bool storedOnly = false)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var track = await db.LibraryTracks.AsNoTracking().SingleOrDefaultAsync(track => track.Id == id && track.Protocol == "jellyfin", token);
        if (track == null) return;
        var current = await index.LibraryIsCurrentAsync(id, LibraryRevision(track), token);
        if (current && !storedOnly) return;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == track.OwnerUserId && user.Enabled, token);
        if (user == null || string.IsNullOrWhiteSpace(settings.Value.Url) || string.IsNullOrWhiteSpace(settings.Value.ApiKey)) return;
        var context = Context(user, token);
        var song = new Song
        {
            Id = track.BackendItemId,
            IsLocal = true,
            Title = track.Title,
            Artist = track.AlbumArtist ?? "",
            Artists = string.IsNullOrWhiteSpace(track.AlbumArtist) ? [] : [track.AlbumArtist],
            Album = track.Album ?? "",
            CanonicalRecordingId = track.CanonicalRecordingId,
            Duration = track.DurationMilliseconds is { } duration ? (int)(duration / 1000) : null,
            ProviderIds = JsonSerializer.Deserialize<Dictionary<string, string>>(track.ProviderIdsJson) ?? new()
        };
        song.SpotifyId = song.ProviderIds.GetValueOrDefault("spotify");
        if (current && storedOnly)
        {
            await Task.Delay(Pace, token);
            await ReadStoredAsync(context, song, track.CanonicalRecordingId, token);
            return;
        }
        await Task.Delay(Pace, token);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.Value.Url.TrimEnd('/') + "/"),
            $"Audio/{Uri.EscapeDataString(track.BackendItemId)}/Lyrics?UserId={Uri.EscapeDataString(user.BackendPrincipalId)}"));
        request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(settings.Value.ApiKey));
        using var client = clients.CreateClient(JellyfinLibraryCatalogScanner.HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
        using var lyricsResponse = response;
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound && !response.IsSuccessStatusCode) return;
        RichLyrics? rich = null;
        if (response.IsSuccessStatusCode)
        {
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return;
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            rich = RichLyricsMapper.Map(BackendLyrics.ParseJellyfin(body.RootElement));
        }
        await index.RemoveLibrarySourceAsync(context.BackendInstanceId, song.Id, token);
        if (rich != null) await index.StoreAsync(context, song, track.CanonicalRecordingId, rich, sharedLibrary: true);
        await ReadStoredAsync(context, song, track.CanonicalRecordingId, token);
        await index.MarkLibraryAsync(id, LibraryRevision(track), token);
    }

    private async Task RefreshRecordingAsync(Guid owner, Guid recording, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && user.Enabled && user.BackendType == "jellyfin", token);
        if (user == null) return;
        var context = Context(user, token);
        var songs = await identities.GetStoredSongsAsync(context.RequireActor(), recording.ToString("D"), token);
        if (songs.Count == 0 && await gateway.GetStoredSongAsync(context, "allstarr", recording.ToString("D")) is { } retained) songs = [retained];
        foreach (var song in songs.Take(50))
        {
            await Task.Delay(Pace, token);
            await ReadStoredAsync(context, song, recording, token);
        }
        var native = (await index.SnapshotAsync(context)).Where(document => document.RecordingId == recording && document.Song.IsLocal)
            .DistinctBy(document => document.ItemId).Take(50).ToArray();
        foreach (var document in native) await ReadStoredAsync(context, document.Song, recording, token);
    }

    private async Task RefreshSongAsync(Guid owner, string itemId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && user.Enabled && user.BackendType == "jellyfin", token);
        if (user == null) return;
        var context = Context(user, token);
        var document = (await index.SnapshotAsync(context)).Where(document => document.ItemId == itemId)
            .OrderByDescending(document => document.Owner == owner).ThenByDescending(document => document.UpdatedAt).FirstOrDefault();
        Song? song = document?.Song;
        Guid? recording = document?.RecordingId;
        await Task.Delay(Pace, token);
        if (song == null && !itemId.StartsWith("ext-", StringComparison.Ordinal))
        {
            var native = await library.FindByBackendItemAsync(context, itemId, token);
            if (native != null)
            {
                recording = native.CanonicalRecordingId;
                song = new Song
                {
                    Id = native.BackendItemId,
                    IsLocal = true,
                    Title = native.Title,
                    Artist = native.AlbumArtist ?? "",
                    Artists = string.IsNullOrWhiteSpace(native.AlbumArtist) ? [] : [native.AlbumArtist],
                    Album = native.Album ?? "",
                    CanonicalRecordingId = recording,
                    SpotifyId = native.ProviderTrackIds.GetValueOrDefault("spotify"),
                    Duration = native.DurationMilliseconds is { } duration ? (int)(duration / 1000) : null,
                    ProviderIds = native.ProviderTrackIds.ToDictionary(pair => pair.Key, pair => pair.Value)
                };
            }
        }
        song ??= (await identities.GetStoredSongsAsync(context.RequireActor(), itemId, token))
            .FirstOrDefault(candidate => LyricsSearchIndex.ItemKey(candidate) == itemId);
        if (song == null) throw new KeyNotFoundException("Stored song metadata is unavailable.");
        await ReadStoredAsync(context, song, recording ?? song.CanonicalRecordingId, token);
    }

    private async Task ReadStoredAsync(ProtocolExecutionContext context, Song song, Guid? recording, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owner = context.Principal!.UserId;
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(account => account.Enabled &&
            (account.OwnerUserId == null || account.OwnerUserId == owner) &&
            (account.SecretReferenceId == null || db.SecretReferences.Any(secret => secret.Id == account.SecretReferenceId && secret.RevokedAt == null))).ToArrayAsync(token);
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(link => link.CanonicalRecordingId == recording &&
            (link.ProviderAccountId == null || accounts.Select(account => account.Id).Contains(link.ProviderAccountId.Value))).ToArrayAsync(token);
        var policy = await policies.ResolveForUserAsync(owner, token);
        var endpoint = (await runtime.GetAsync("LyricsPlus:BaseUrl", token)).NormalizedValue;
        var selectedAccounts = new List<ProviderAccountRecord>();
        foreach (var provider in accounts.Select(account => account.ProviderId).Distinct(StringComparer.Ordinal))
        {
            var plan = await router.PlanAsync<IProviderLyricsCapability>(LyricsSearchIndex.SourceRequest(context, provider));
            if (plan.Candidates.FirstOrDefault()?.Context.Account is { } account)
                selectedAccounts.AddRange(accounts.Where(candidate => candidate.Id == account.AccountId));
        }
        var stored = new List<RichLyrics>();
        var itemId = LyricsSearchIndex.ItemKey(song);
        var observedCache = new ObservedLyricsCache(cache, key =>
        {
            if (itemId != null) changes.WatchCacheKey(key, owner, itemId);
        });
        var recordingDuration = await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == recording)
            .Select(item => item.DurationMilliseconds).SingleOrDefaultAsync(token);
        var automatic = recording.HasValue && refreshStore != null ? await refreshStore.RetainedAsync(context, recording.Value) : null;
        var storedInfo = new List<allstarr.Models.Lyrics.LyricsInfo>();
        await StoredItemLyrics.ReadAsync(observedCache, song, recording, null, null, policy, endpoint, context, selectedAccounts, links, token,
            stored.Add, recordingDuration, automatic, storedInfo.Add);
        var originalTimingRequired = false;
        if (song.IsLocal && policy.IsAutomatic("backend") && await index.ReadBackendAsync(context, song) is { } backend)
        {
            if (LyricsRefreshStore.FromIndexedLines(backend, song) is { } baseline) storedInfo.Add(baseline);
            else originalTimingRequired = true;
        }
        if (policy.IsEnabled("kept"))
        {
            var keptIdentities = song.IsLocal ? song.ProviderIds.Select(pair => new KeptLyricsIdentity(pair.Key, pair.Value)).ToArray() :
                song.ExternalProvider is { } provider && song.ExternalId is { } external ? new[] { new KeptLyricsIdentity(provider, external) } : [];
            if (keptIdentities.Length > 0 || song.CanonicalRecordingId.HasValue)
            {
                var keptInfo = await kept.ReadAsync(context, song, keptIdentities);
                if (RichLyricsMapper.Map(keptInfo) is { } retained)
                {
                    stored.Add(retained);
                    storedInfo.Add(keptInfo!);
                }
            }
        }
        foreach (var rich in stored)
        {
            var account = selectedAccounts.FirstOrDefault(account => account.ProviderId == LyricsVersions.ProviderId(rich.Source.Id));
            await index.StoreAsync(context, song, recording, rich,
                expectedProvenance: LyricsSearchIndex.AccountProvenance(account?.Id, account?.Revision));
        }
        if (recording.HasValue && refreshStore != null && !originalTimingRequired)
        {
            var observedSong = MusicLyrics.ForLookup(song);
            observedSong.CanonicalRecordingId = recording;
            var info = storedInfo.Where(info => info.Source != null && policy.IsAutomatic(LyricsVersions.ProviderId(info.Source)))
                .OrderByDescending(info => LyricsQuality.Evaluate(info, song, recordingDuration)?.Score ?? -1).FirstOrDefault();
            if (info != null)
            {
                var account = selectedAccounts.FirstOrDefault(account => account.ProviderId == LyricsVersions.ProviderId(info.Source!));
                info.CacheProvenance = LyricsSearchIndex.AccountProvenance(account?.Id, account?.Revision);
            }
            await refreshStore.ObserveAsync(context, observedSong, info, recordingDuration);
        }
        automatic = recording.HasValue && refreshStore != null ? await refreshStore.RetainedAsync(context, recording.Value) : null;
        if (RichLyricsMapper.Map(automatic) is { } selectedAutomatic)
            await index.StoreAsync(context, song, recording, selectedAutomatic, expectedProvenance: automatic?.CacheProvenance);
        await index.ReconcileStoredAsync(context, song, stored.Select(rich => rich.Source.Id).ToHashSet(StringComparer.Ordinal), observedCache.UnavailableSources);
    }

    private static long LibraryRevision(LibraryTrackRecord track) => BinaryPrimitives.ReadInt64LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes(LibraryIndexService.TrackFingerprint(track))));

    private static ProtocolExecutionContext Context(UserRecord user, CancellationToken token) => new(ProtocolKind.Jellyfin,
        user.BackendInstanceId, user.BackendPrincipalId, new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId,
            user.BackendPrincipalId, user.DisplayName, user.IsAdmin), "library-lyrics", DateTimeOffset.UtcNow.AddMinutes(5), token);
}
