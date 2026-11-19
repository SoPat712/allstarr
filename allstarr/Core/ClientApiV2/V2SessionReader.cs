using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;

namespace allstarr.Core.ClientApiV2;

public sealed class V2SessionReader(IDurableRuntimeSettings settings, LibraryProviderResolver library, ClientLibraryAdditions additions)
{
    public async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var listener = execution.Principal!;
        var address = (await settings.GetAsync("Admin:Oidc:PublicUrl", execution.CancellationToken)).NormalizedValue.Trim();
        var mark = V2ProviderUrls.For(await library.ResolveAsync(http, execution));
        var adding = await additions.CapabilitiesAsync(execution, execution.CancellationToken);
        return new(200, new V2SessionResponse(new(listener.UserId, listener.DisplayName), AppVersion.Version,
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["tracks"] = true,
                ["albums"] = true,
                ["artists"] = true,
                ["batch"] = true,
                ["playback"] = true,
                ["events"] = true,
                ["artistLinks"] = true,
                ["lyricsTranslation"] = (bool)(await settings.GetAsync("Lyrics:Translation:Enabled", execution.CancellationToken)).Value,
                ["autoplay"] = true,
                ["trackFacts"] = true,
                ["lyricsSources"] = true,
                ["trackMatch"] = true,
                ["appearances"] = true,
                ["similarAlbums"] = true,
                ["playbackStatus"] = true,
                ["mixes"] = true,
                ["releases"] = true,
                ["libraryAdditions"] = true,
                ["artistFollow"] = true,
                ["reports"] = true,
                ["karaokeModel"] = true,
                ["providerIcons"] = true,
                ["history"] = true,
                ["historyImports"] = true,
                ["historyRecheck"] = true,
                ["stats"] = true,
                ["sound"] = true,
                ["taste"] = true,
                ["preferences"] = true,
                ["myLibraries"] = true,
                ["search"] = true,
                ["lyricsSearch"] = true,
                ["lyricsSearchServices"] = true,
                ["lyricsSearchServicesResolve"] = true,
                ["playbackRequestStatus"] = true,
                ["tracksFacts"] = true,
                ["mixRows"] = true,
                ["historyRows"] = true,
                ["trackLyrics"] = true,
                // A library artist's links list unproven same-named service artists; a service artist names its library artist.
                ["artistCandidates"] = true,
                ["artistIdentity"] = true,
                ["albumIdentity"] = true,
                // A service song's measured loudness arrives as trackGainDb, announced by track.updated with changed ["loudness"].
                ["trackLoudness"] = true
            }, string.IsNullOrEmpty(address) ? null : new(address), new(mark.Id, mark.Name, mark.IconUrl, mark.GlyphUrl),
            new(adding.CanAddDirectly, adding.RequiresApproval, adding.DefaultUnit) { SetupNeeded = adding.SetupNeeded }));
    }
}
