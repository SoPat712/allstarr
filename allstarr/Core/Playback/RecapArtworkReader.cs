using allstarr.Core.Intelligence;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

// Artist pictures come from the identities and covers in the listener's own plays.
public sealed class RecapArtworkReader(IProtocolProviderGateway gateway, IMediaAssetResolver media, IHttpClientFactory clients,
    IDbContextFactory<AllstarrDbContext>? factory = null, JellyfinProxyService? jellyfin = null,
    IBackendLibraryAccessResolver? libraryAccess = null, IHttpContextAccessor? http = null, TimeProvider? time = null) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly SemaphoreSlim creditsGate = new(1, 1);
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

    internal bool IsNegative(MediaAssetIdentity identity) => cache.TryGetValue(identity, out DateTimeOffset until) &&
        until > (time ?? TimeProvider.System).GetUtcNow();

    private const int MaximumBytes = 8 * 1024 * 1024, MinimumBytes = 1024;

    public async Task<ResolvedMediaAsset?> ReadAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album, CancellationToken token,
        string? libraryArtistId = null)
    {
        try
        {
            return await ResolveAsync(protocol, artist, title, album, libraryArtistId, TimeSpan.FromSeconds(5))
                .WaitAsync(TimeSpan.FromSeconds(5), time ?? TimeProvider.System, token);
        }
        catch (TimeoutException) { return null; }
    }

    internal Task<ResolvedMediaAsset?> PrepareAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album,
        CancellationToken token, string? libraryArtistId = null) =>
        ResolveAsync(protocol, artist, title, album, libraryArtistId, TimeSpan.FromSeconds(30), token);

    private async Task<ResolvedMediaAsset?> ResolveAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album,
        string? libraryArtistId, TimeSpan budget, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 500 || title?.Length > 500 || album?.Length > 500 || libraryArtistId?.Length > 500)
            throw new ArgumentException("The artwork request is invalid.");
        var identity = Identity(protocol.RequireActor().EffectiveUserId, artist, title, album, libraryArtistId);
        if (IsNegative(identity)) return null;
        var empty = false;
        var asset = await media.ResolveAsync(identity, async _ =>
        {
            // The media resolver shares this fetch; an image loader disconnecting must not cancel other waiters.
            var clock = time ?? TimeProvider.System;
            using var timeout = new CancellationTokenSource(budget, clock);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
            var deadline = clock.GetUtcNow().Add(budget);
            if (protocol.Deadline < deadline) deadline = protocol.Deadline;
            var context = new ProtocolExecutionContext(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId,
                protocol.Principal, protocol.CorrelationId, deadline, stop.Token, protocol.Client)
            {
                Policy = protocol.Policy,
                IsReadOnly = protocol.IsReadOnly,
                RequestedItemId = protocol.RequestedItemId,
                UseStoredBackendCredentials = protocol.UseStoredBackendCredentials,
                BackendCredentialReferenceId = protocol.BackendCredentialReferenceId
            };
            try
            {
                // Some providers do work before returning a task; that must stay inside the wait budget too.
                var work = Task.Run(() => ReadSourceAsync(context, artist, title, album, libraryArtistId, context.CancellationToken), stop.Token);
                var source = title == null && album == null ? await work : await work.WaitAsync(stop.Token);
                empty = source == null || source.Bytes.Length < MinimumBytes;
                return source;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
        }, MaximumBytes, CancellationToken.None);
        token.ThrowIfCancellationRequested();
        // Some sources hold a blank picture for an artist; use the placeholder instead.
        if (asset is { Bytes.Length: >= MinimumBytes }) return asset;
        if (empty || asset != null)
            cache.Set(identity, (time ?? TimeProvider.System).GetUtcNow() + KeptFor,
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = KeptFor });
        return null;
    }

    public void Dispose() { cache.Dispose(); creditsGate.Dispose(); }

    internal static MediaAssetIdentity Identity(Guid? owner, string artist, string? title, string? album, string? libraryArtistId)
    {
        var kind = title != null ? "recap-song" : album != null ? "recap-album" : "recap-artist-identity";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Key(artist)}|{Key(title)}|{Key(album)}|{libraryArtistId}")));
        return new(owner, null, "recap", kind, id, Width: 192);
    }

    private async Task<MediaAssetSource?> ReadSourceAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album,
        string? libraryArtistId, CancellationToken token) => title == null && album == null
        ? await ArtistAsync(protocol, artist, libraryArtistId, token)
        : await FetchAsync(await FindAsync(protocol, artist, title, album), token);

    private async Task<MediaAssetSource?> ArtistAsync(ProtocolExecutionContext protocol, string artist, string? requestedId, CancellationToken token)
    {
        if (factory == null) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var scope = new allstarr.Core.Intelligence.IntelligenceScope(protocol.Principal!.UserId, protocol.Protocol.ToString().ToLowerInvariant(), protocol.BackendInstanceId);
        var stamp = await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == scope.OwnerUserId)
            .GroupBy(item => item.OwnerUserId).Select(group => new
            { Plays = group.LongCount(), Finished = group.LongCount(item => item.State == ListeningEventState.Completed) }).SingleOrDefaultAsync(token);
        var key = (scope, stamp?.Plays ?? 0, stamp?.Finished ?? 0);
        ListeningArtist[] all;
        await creditsGate.WaitAsync(token);
        try
        {
            if (!cache.TryGetValue(key, out ListeningArtist[]? saved))
            {
                saved = (await ListeningStatsService.ArtistCreditsAsync(db, scope, ListeningStatsService.CountedPlays(db, scope), token,
                    jellyfin, libraryAccess, http?.HttpContext?.Request.Headers)).Values.SelectMany(items => items).ToArray();
                token.ThrowIfCancellationRequested();
                cache.Set(key, saved, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = KeptFor });
            }
            all = saved!;
        }
        finally { creditsGate.Release(); }
        var credits = all.Where(item => item.Name.Equals(artist, StringComparison.OrdinalIgnoreCase)).ToArray();
        var ids = credits.Select(item => item.Id).OfType<string>().Where(id => !id.StartsWith("ext-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        var nativeId = requestedId != null ? ids.FirstOrDefault(id => id == requestedId) : ids.Length == 1 ? ids[0] : null;
        if (jellyfin != null && nativeId != null)
        {
            var (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(protocol.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(nativeId)}",
                clientHeaders: http?.HttpContext?.Request.Headers, cancellationToken: token);
            using (body)
            {
                if (status == 200 && body != null && body.RootElement.TryGetProperty("Id", out var id) && id.GetString() == nativeId &&
                    body.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "MusicArtist")
                {
                    var tag = body.RootElement.TryGetProperty("ImageTags", out var tags) && tags.TryGetProperty("Primary", out var primary) ? primary.GetString() : null;
                    var image = await jellyfin.GetImageAsync(nativeId, imageTag: tag, clientHeaders: http?.HttpContext?.Request.Headers).WaitAsync(token);
                    if (image.Body is { Length: >= MinimumBytes } && image.ContentType != null) return new(image.Body, image.ContentType);
                }
            }
        }
        foreach (var credit in credits.DistinctBy(item => (item.LibraryCover, item.CoverUrl)))
        {
            token.ThrowIfCancellationRequested();
            if (jellyfin != null && credit.LibraryCover is { } cover)
            {
                var image = await jellyfin.GetImageAsync(cover, clientHeaders: http?.HttpContext?.Request.Headers).WaitAsync(token);
                if (image.Body is { Length: >= MinimumBytes } && image.ContentType != null) return new(image.Body, image.ContentType);
            }
            if (await FetchAsync(credit.CoverUrl, token) is { } asset) return asset;
        }
        return null;
    }

    private async Task<MediaAssetSource?> FetchAsync(string? url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
        using var response = await clients.CreateClient().GetAsync(uri, token);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
            response.Content.Headers.ContentLength > MaximumBytes) return null;
        await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
        return new(await response.Content.ReadAsByteArrayAsync(token), contentType);
    }

    private async Task<string?> FindAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album)
    {
        // One quick source covers nearly everything; the rest are only asked when it comes up empty.
        foreach (var source in new[] { "deezer", null })
        {
            var query = title != null ? $"{artist} {title}" : $"{artist} {album}";
            protocol.CancellationToken.ThrowIfCancellationRequested();
            var found = await gateway.SearchAsync(protocol, query, title != null ? 8 : 0, title == null ? 8 : 0, 0, source)
                .WaitAsync(protocol.CancellationToken);
            protocol.CancellationToken.ThrowIfCancellationRequested();
            var url = title != null
                ? found.Songs.Where(song => Credited(song.Artist, artist) && Close(song.Title, title)).Select(song => song.CoverArtUrlLarge ?? song.CoverArtUrl).FirstOrDefault(Usable)
                : found.Albums.Where(item => Credited(item.Artist, artist) && Close(item.Title, album!)).Select(item => item.CoverArtUrl).FirstOrDefault(Usable);
            if (url != null) return url;
        }
        return null;
    }

    private static bool Usable(string? url) => !string.IsNullOrWhiteSpace(url);
    private static bool Credited(string? credit, string artist) => Key(credit) == Key(artist) ||
        FuzzyMatcher.SplitCredits(credit).Any(name => Key(name) == Key(artist)) || FuzzyMatcher.SplitCredits(artist).Any(name => Key(name) == Key(credit));
    private static bool Close(string? found, string wanted)
    {
        var (left, right) = (Key(found), Key(wanted));
        return left.Length > 0 && right.Length > 0 && (left == right || left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal));
    }
    private static string Key(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
