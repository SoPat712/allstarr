using allstarr.Core.Protocols;
using allstarr.Core.Settings;

namespace allstarr.Core.ClientApiV2;

public sealed class V2SessionReader(IDurableRuntimeSettings settings)
{
    public async Task<V2Result> ReadAsync(ProtocolExecutionContext execution)
    {
        var listener = execution.Principal!;
        var address = (await settings.GetAsync("Admin:Oidc:PublicUrl", execution.CancellationToken)).NormalizedValue.Trim();
        return new(200, new V2SessionResponse(new(listener.UserId, listener.DisplayName), AppVersion.Version,
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["tracks"] = true,
                ["albums"] = true,
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
                ["playbackStatus"] = true
            }, string.IsNullOrEmpty(address) ? null : new(address)));
    }
}
