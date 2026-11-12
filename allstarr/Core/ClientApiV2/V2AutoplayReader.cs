using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Core.ClientApiV2;

/// <summary>Continues a track through the existing mix sources, retaining the page when a later source times out.</summary>
public sealed class V2AutoplayReader(V2TrackReader tracks, MusicMixService mixes, IProtocolProviderGateway providers,
    JellyfinProxyService jellyfin, IEffectiveProviderPolicyResolver policies, TimeProvider clock, ILogger<V2AutoplayReader> logger)
{
    private const int SourceLimit = 200;
    private const int MaximumSeen = 200;
    private static readonly JsonSerializerOptions CursorJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal sealed record Cursor(string Seed, int Source, int Offset, string[] Seen);

    internal static bool TryCursor(string id, string? value, out Cursor cursor)
    {
        cursor = new(id, 0, 0, []);
        if (!V2Ids.TryDecode(id, V2IdKind.Track, out _)) return false;
        if (value == null) return true;
        if (value.Length is 0 or > 7600 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))) return false;
        try
        {
            var parts = value.Split('.');
            if (parts.Length != 2) return false;
            var bytes = WebEncoders.Base64UrlDecode(parts[0]);
            if (WebEncoders.Base64UrlEncode(bytes) != parts[0] || Fingerprint(bytes) != parts[1]) return false;
            var read = JsonSerializer.Deserialize<Cursor>(bytes, CursorJson);
            if (read == null || read.Seed == null || !read.Seed.StartsWith("trk_", StringComparison.Ordinal) ||
                !V2Ids.TryDecode(read.Seed, V2IdKind.Track, out _) || read.Source is < 0 or > 3 || read.Offset is < 0 or >= SourceLimit ||
                read.Seen == null || read.Seen.Length is 0 or > MaximumSeen || read.Seen.Distinct(StringComparer.Ordinal).Count() != read.Seen.Length ||
                read.Seen.Any(key => key == null || key.Length != 22 || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) ||
                !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(read, CursorJson))) return false;
            cursor = read;
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException) { return false; }
    }

    internal async Task<(V2AutoplayResponse Page, bool Partial)> ReadAsync(HttpContext http, ProtocolExecutionContext execution,
        V2Track seed, string source, Cursor cursor, int limit)
    {
        var deadline = clock.GetUtcNow().AddSeconds(5);
        if (execution.Deadline < deadline) deadline = execution.Deadline;
        using var timeout = new CancellationTokenSource(deadline > clock.GetUtcNow() ? deadline - clock.GetUtcNow() : TimeSpan.Zero, clock);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, execution.CancellationToken);
        var context = Bounded(execution, deadline, caller.Token);
        var result = new List<V2Track>();
        var seen = cursor.Seen.ToHashSet(StringComparer.Ordinal);
        seen.UnionWith(Keys(seed));
        var stage = cursor.Source;
        var offset = cursor.Offset;
        var partial = false;
        try
        {
            context = context with { Policy = execution.Policy ?? await policies.ResolveForUserAsync(execution.RequireActor().EffectiveUserId!.Value, caller.Token).WaitAsync(caller.Token) };
            while (stage < 4 && result.Count < limit && seen.Count <= MaximumSeen - 4)
            {
                // Leave time to shape songs retained by radio when its own source budget expires.
                var sourceDeadline = clock.GetUtcNow().AddSeconds(3);
                if (deadline < sourceDeadline) sourceDeadline = deadline;
                using var sourceTimeout = new CancellationTokenSource(sourceDeadline > clock.GetUtcNow() ? sourceDeadline - clock.GetUtcNow() : TimeSpan.Zero, clock);
                using var sourceStop = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, sourceTimeout.Token);
                var radio = stage == 0 && source.StartsWith("ext-", StringComparison.Ordinal);
                var bounded = Bounded(context, sourceDeadline, radio ? caller.Token : sourceStop.Token);
                IReadOnlyList<string> ids;
                try
                {
                    ids = await SourceAsync(http, bounded, seed, source, stage).WaitAsync(radio ? caller.Token : sourceStop.Token);
                }
                catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested)
                {
                    partial = true;
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "Autoplay source {Source} is unavailable", stage);
                    stage++;
                    offset = 0;
                    continue;
                }
                var interrupted = sourceTimeout.IsCancellationRequested || clock.GetUtcNow() >= sourceDeadline;
                // A completed radio can return its gathered rows just as its timer fires.
                while (offset < ids.Count && result.Count < limit && seen.Count <= MaximumSeen - 4)
                {
                    var id = ids[offset];
                    var page = await tracks.ReadAsync(http, context, [id], false).WaitAsync(caller.Token);
                    foreach (var track in page.Tracks)
                    {
                        var keys = Keys(track).ToArray();
                        if (keys.Any(seen.Contains)) continue;
                        seen.UnionWith(keys);
                        result.Add(track);
                    }
                    offset++;
                }
                if (interrupted) { partial = true; break; }
                if (offset >= ids.Count) { stage++; offset = 0; }
            }
        }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested) { partial = true; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Autoplay page is incomplete");
            partial = true;
        }
        execution.CancellationToken.ThrowIfCancellationRequested();
        var next = stage < 4 && offset < SourceLimit && seen.Count <= MaximumSeen - 4
            ? Encode(new(Identity(seed), stage, offset, seen.Order(StringComparer.Ordinal).ToArray())) : null;
        return (new(result, next), partial);
    }

    private async Task<IReadOnlyList<string>> SourceAsync(HttpContext http, ProtocolExecutionContext context, V2Track seed, string source, int stage)
    {
        var parsed = LocalLibraryService.ParseExternalResource(source);
        if (parsed.provider == "allstarr")
        {
            var service = seed.ExternalIds.FirstOrDefault(pair => pair.Key is not ("isrc" or "musicbrainzRecording"));
            var candidate = $"ext-{service.Key}-song-{service.Value}";
            if (service.Key != null && V2Ids.IsValidSource(candidate, V2IdKind.Track))
            {
                source = candidate;
                parsed = LocalLibraryService.ParseExternalResource(source);
            }
        }
        if (parsed.isExternal)
        {
            if (stage == 0)
            {
                var radio = await mixes.RadioAsync(context, parsed.provider!, "song", parsed.externalId!, source, 50, new NativeSearch(http, context, jellyfin));
                return radio.Select(row => row.Native?.Id ?? row.Song?.Id).OfType<string>().ToArray();
            }
            if (stage == 1)
                return (await mixes.SimilarSongsAsync(context, parsed.provider!, parsed.externalId!, source, SourceLimit))
                    .Where(song => context.Policy?.Includes(song) != false).Select(song => song.Id).ToArray();
        }
        if (stage < 2)
            return await NativeIdsAsync(http, context, $"Items/{Uri.EscapeDataString(source)}/{(stage == 0 ? "InstantMix" : "Similar")}",
                new() { ["userId"] = context.VerifiedBackendPrincipalId, ["limit"] = SourceLimit.ToString() });
        var reference = stage == 2 ? seed.Artists.FirstOrDefault(artist => artist.Id != null)?.Id : seed.Album?.Id;
        if (!V2Ids.TryDecode(reference, stage == 2 ? V2IdKind.Artist : V2IdKind.Album, out var decoded) || decoded.Source == null) return [];
        var related = LocalLibraryService.ParseExternalResource(decoded.Source);
        if (related.isExternal)
        {
            var songs = stage == 2 ? await providers.GetArtistTracksAsync(context, related.provider!, related.externalId!)
                : (await providers.GetAlbumAsync(context, related.provider!, related.externalId!))?.Songs ?? [];
            return songs.Where(song => context.Policy?.Includes(song) != false).Take(SourceLimit).Select(song => song.Id).ToArray();
        }
        return await NativeIdsAsync(http, context, $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items",
            new()
            {
                [stage == 2 ? "artistIds" : "parentId"] = decoded.Source,
                ["includeItemTypes"] = "Audio",
                ["recursive"] = "true",
                ["limit"] = SourceLimit.ToString(),
                ["sortBy"] = stage == 2 ? "SortName" : "ParentIndexNumber,IndexNumber",
                ["sortOrder"] = "Ascending"
            });
    }

    private async Task<IReadOnlyList<string>> NativeIdsAsync(HttpContext http, ProtocolExecutionContext context, string path, Dictionary<string, string> query)
    {
        var (body, status) = await jellyfin.GetJsonAsync(path, query, http.Request.Headers, context.CancellationToken);
        using var document = body;
        if (status != 200 || document == null) throw new HttpRequestException("Library mix is unavailable.");
        return document.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("Id").GetString()!).Where(id => V2Ids.IsValidSource(id, V2IdKind.Track)).Take(SourceLimit).ToArray() : [];
    }

    private static IEnumerable<string> Keys(V2Track track)
    {
        yield return Fingerprint(Encoding.UTF8.GetBytes("id:" + Identity(track)));
        foreach (var kind in new[] { "isrc", "musicbrainzRecording" })
            if (track.ExternalIds.TryGetValue(kind, out var value) && !string.IsNullOrWhiteSpace(value))
                yield return Fingerprint(Encoding.UTF8.GetBytes(kind + ":" + value.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant()));
    }

    internal static string Identity(V2Track track) =>
        V2Ids.TryDecode(track.Id, V2IdKind.Track, out var id) && id.Recording == null && Guid.TryParse(id.Source, out var native)
            ? V2Ids.TrackSource(native.ToString("N")) : track.Id;

    // This checksum detects damaged cursors; their unsigned contents are always treated as untrusted input.
    private static string Fingerprint(byte[] bytes) => WebEncoders.Base64UrlEncode(SHA256.HashData(bytes)[..16]);

    private static string Encode(Cursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, CursorJson);
        return WebEncoders.Base64UrlEncode(bytes) + "." + Fingerprint(bytes);
    }

    internal static ProtocolExecutionContext Bounded(ProtocolExecutionContext context, DateTimeOffset deadline, CancellationToken token) =>
        new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal, context.CorrelationId, deadline, token, context.Client)
        { Policy = context.Policy, IsReadOnly = context.IsReadOnly, UseStoredBackendCredentials = context.UseStoredBackendCredentials, BackendCredentialReferenceId = context.BackendCredentialReferenceId };

    private sealed class NativeSearch(HttpContext http, ProtocolExecutionContext context, JellyfinProxyService proxy) : INativeMusicSearch<string>
    {
        public Task<IReadOnlyList<NativeMusicItem<string>>> SearchAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NativeMusicItem<string>>>([]);

        public async Task<NativeMusicItem<string>?> GetSongAsync(string id, CancellationToken cancellationToken)
        {
            var (body, status) = await proxy.GetJsonAsync($"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(id)}", null, http.Request.Headers, cancellationToken);
            using var document = body;
            if (status != 200 || document == null || !document.RootElement.TryGetProperty("Id", out var itemId) || itemId.GetString() != id ||
                !document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "Audio" ||
                !document.RootElement.TryGetProperty("Name", out var name)) return null;
            var rating = document.RootElement.TryGetProperty("OfficialRating", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new(MusicItemKind.Song, id, name.GetString() ?? "", null, id)
            { ExplicitContentLyrics = CleanOnlyProtocolContent.Rating(rating) };
        }
    }
}
