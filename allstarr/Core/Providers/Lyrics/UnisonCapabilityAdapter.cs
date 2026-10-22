using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class UnisonCapabilityAdapter(
    HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null)
    : PublicLyricsCapabilityAdapter(http, cache, lyricsTtl)
{
    public const string HttpClientName = "UnisonLyrics";
    private static readonly SemaphoreSlim _requests = new(1, 1);
    private static long _lastRequest;
    public override string ProviderId => "unison";
    protected override bool RequiresArtist => true;

    protected override async Task BeforeRequestAsync(CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try
        {
            // Public reads allow 120 requests per minute per IP.
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastRequest);
            if (_lastRequest != 0 && elapsed < TimeSpan.FromMilliseconds(510))
                await Task.Delay(TimeSpan.FromMilliseconds(510) - elapsed, token);
            _lastRequest = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        finally { _requests.Release(); }
    }

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0) return Unavailable();
        var response = await GetAsync(new("https://unison.boidu.dev/lyrics?" + MetadataQuery(request, "song")), token);
        if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
        if (response.Content == null) return Unavailable();
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)) return ContractFailure();
        if (success.ValueKind == JsonValueKind.False)
            return root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() == "NOT_FOUND" ? Unavailable() : ContractFailure();
        if (success.ValueKind != JsonValueKind.True || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String) return ContractFailure();
        var text = lyrics.GetString()!;
        if (string.IsNullOrWhiteSpace(text)) return Unavailable();
        if (format.GetString() == "ttml") return ParseTtml(text);
        if (format.GetString() is not ("plain" or "lrc")) return ContractFailure();
        var plain = format.GetString() == "plain" ||
            data.TryGetProperty("syncType", out var sync) && sync.ValueKind == JsonValueKind.String && sync.GetString() == "plain";
        if (!plain && format.GetString() != "lrc") return ContractFailure();
        if (!plain && allstarr.Core.Music.MusicLyrics.ParseSyncedLines(text).Count == 0) return ContractFailure();
        return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available, ProviderId,
            plain ? ProviderLyricsFormat.PlainText : ProviderLyricsFormat.LineTimed, text, "sha256:" + Hash(text)));
    }
}
