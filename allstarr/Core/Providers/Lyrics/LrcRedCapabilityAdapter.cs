using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class LrcRedCapabilityAdapter(
    HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null)
    : PublicLyricsCapabilityAdapter(http, cache, lyricsTtl)
{
    public const string HttpClientName = "LrcRed";
    public override string ProviderId => "lrcred";

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        ProviderOutcome<ProviderLyricsResult>? direct = null;
        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            direct = await ReadTtmlAsync(request.Isrc, token);
            if (direct.IsSuccess && direct.RequireValue().Content != null ||
                direct.Error is { Kind: ProviderErrorKind.RateLimited }) return direct;
        }
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0) return direct ?? Unavailable();
        var response = await GetAsync(new("https://lrc.red/match.json?" + MetadataQuery(request, "track")), token);
        if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
        if (response.Content == null) return direct is { IsSuccess: false } ? direct : Unavailable();
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return ContractFailure();
        if (hits.GetArrayLength() == 0) return direct is { IsSuccess: false } ? direct : Unavailable();
        if (hits[0].ValueKind != JsonValueKind.Object || !hits[0].TryGetProperty("isrc", out var value) || value.ValueKind != JsonValueKind.String) return ContractFailure();
        var isrc = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(isrc) || isrc == request.Isrc) return direct is { IsSuccess: false } ? direct : Unavailable();
        return await ReadTtmlAsync(isrc, token);
    }

    private async Task<ProviderOutcome<ProviderLyricsResult>> ReadTtmlAsync(string isrc, CancellationToken token)
    {
        var response = await GetAsync(new($"https://lrc.red/s/{Uri.EscapeDataString(isrc)}.ttml"), token);
        return response.Error != null ? ProviderOutcome<ProviderLyricsResult>.Failure(response.Error)
            : response.Content == null ? Unavailable() : ParseTtml(response.Content);
    }
}
