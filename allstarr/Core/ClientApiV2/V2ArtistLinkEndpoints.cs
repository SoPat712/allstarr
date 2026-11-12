using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApiV2;

/// <summary>One service artist tied to a library artist, and why.</summary>
public sealed record V2ArtistLink(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("aliases"), Description("The same artist's older id, as search and artist pages use it today.")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceName")] string SourceName,
    [property: JsonPropertyName("by"), Description("`songs` when matched songs tie them, `fix` when a person said so.")] string By,
    [property: JsonPropertyName("scope"), Description("For a fix: `household` when an administrator set it for everyone, `you` when it is this listener's own.")] string? Scope);

public sealed record V2ArtistLinksResponse(
    [property: JsonPropertyName("links"), Description("Service artists shown as this library artist.")] IReadOnlyList<V2ArtistLink> Links,
    [property: JsonPropertyName("notSame"), Description("Service artists someone said are a different act; search keeps them apart.")] IReadOnlyList<V2ArtistLink> NotSame);

public sealed record V2ArtistLinkRequest
{
    [JsonPropertyName("same"), Description("`true`: the same artist. `false`: a different act.")]
    public required bool Same { get; init; }
}

/// <summary>
/// "Same artist" and "Not the same": a person's answer when matched songs could not tie a library artist to a service
/// artist, or tied the wrong one. An administrator's answer covers the whole household and a listener's covers only
/// them; a listener's own answer wins over the household's for that listener.
/// </summary>
public sealed class V2ArtistLinkEndpoints(ILibraryArtistLinks index, JellyfinProxyService jellyfin, IProviderRegistry registry)
{
    public async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string libraryArtistId)
    {
        if (execution.Actor?.EffectiveUserId is not { } owner) return new(401, null);
        if (await LibraryArtistNameAsync(http, execution, libraryArtistId) is not { } name) return new(404, null);
        return new(200, await ResponseAsync(owner, name, http.RequestAborted));
    }

    public async Task<V2Result> SetAsync(HttpContext http, ProtocolExecutionContext execution, string libraryArtistId,
        string serviceArtistId, bool? same)
    {
        if (execution.Actor?.EffectiveUserId is not { } owner) return new(401, null);
        if (!TryServiceArtist(serviceArtistId, out var provider, out var artistId)) return new(400, null);
        if (await LibraryArtistNameAsync(http, execution, libraryArtistId) is not { } name) return new(404, null);
        if (same == null && execution.Principal?.IsAdministrator != true)
        {
            // A listener undoes only their own answer; the household's stays, and their own "not the same" overrides it for them.
            var (_, fixes) = await index.ReadPartsAsync(owner, name, http.RequestAborted);
            if (!fixes.Any(fix => !fix.Household && Is(fix, provider, artistId)) && fixes.Any(fix => fix.Household && Is(fix, provider, artistId)))
                return new(403, new ClientApiError("household_fix", "This was set for the whole household. Mark it as not the same to change it for you."));
        }
        if (!await index.SetFixAsync(owner, name, provider, artistId, same, http.RequestAborted)) return new(400, null);
        return new(200, await ResponseAsync(owner, name, http.RequestAborted));
    }

    private async Task<V2ArtistLinksResponse> ResponseAsync(Guid owner, string name, CancellationToken token)
    {
        var (songs, fixes) = await index.ReadPartsAsync(owner, name, token);
        V2ArtistLink Shape(string provider, string artistId, LibraryArtistFix? fix)
        {
            var legacy = ProtocolProviderGateway.ProtocolItemId(new(provider, ProviderResourceKind.Artist, artistId));
            var sourceName = registry.TryGet(provider, out var descriptor) ? descriptor!.DisplayName : provider;
            return new(V2Ids.Artist(legacy), [legacy], provider, sourceName, fix == null ? "songs" : "fix",
                fix == null ? null : fix.Household ? "household" : "you");
        }
        var links = LibraryArtistIndex.Apply(songs, fixes).Select(link =>
            Shape(link.ProviderId, link.ArtistId, fixes.FirstOrDefault(fix => fix.Same && Is(fix, link.ProviderId, link.ArtistId)))).ToArray();
        var notSame = fixes.Where(fix => !fix.Same).Select(fix => Shape(fix.ProviderId, fix.ArtistId, fix)).ToArray();
        return new(links, notSame);
    }

    private static bool Is(LibraryArtistFix fix, string provider, string artistId) =>
        fix.ArtistId == artistId && string.Equals(fix.ProviderId, provider, StringComparison.Ordinal);

    // The library side is a media server artist; its name is what links are kept under.
    private async Task<string?> LibraryArtistNameAsync(HttpContext http, ProtocolExecutionContext execution, string libraryArtistId)
    {
        if (!V2Ids.TryDecode(libraryArtistId, V2IdKind.Artist, out var id) || id.Source is not { } source ||
            source.StartsWith("ext-", StringComparison.Ordinal)) return null;
        var (body, status) = await jellyfin.GetJsonAsync(
            $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(source)}",
            null, http.Request.Headers, http.RequestAborted);
        using (body)
        {
            if (status != 200 || body == null || !body.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MusicArtist" ||
                !body.RootElement.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String) return null;
            return name.GetString();
        }
    }

    private bool TryServiceArtist(string serviceArtistId, out string provider, out string artistId)
    {
        provider = artistId = "";
        if (!V2Ids.TryDecode(serviceArtistId, V2IdKind.Artist, out var id) || id.Source is not { } source) return false;
        var parsed = LocalLibraryService.ParseExternalResource(source);
        if (!parsed.isExternal || parsed.type != "artist" || string.IsNullOrWhiteSpace(parsed.provider) || string.IsNullOrWhiteSpace(parsed.externalId))
            return false;
        provider = registry.ResolveProviderId(AppleMusicClient.NormalizeProviderId(parsed.provider));
        artistId = parsed.externalId;
        return true;
    }
}
