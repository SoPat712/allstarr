using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed partial class GeniusCapabilityAdapter : MetadataLyricsCapabilityAdapter
{
    public const string HttpClientName = "GeniusLyrics";
    private static readonly MetadataLyricsFailureCache Failures = new(TimeProvider.System);
    public override string ProviderId => "genius";

    public GeniusCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null,
        ILogger<GeniusCapabilityAdapter>? logger = null)
        : this(http, cache, TimeProvider.System, Failures, logger, lyricsTtl) { }

    internal GeniusCapabilityAdapter(HttpClient http, IApplicationCache? cache, TimeProvider clock,
        MetadataLyricsFailureCache failures, ILogger? logger = null, Func<TimeSpan>? lyricsTtl = null)
        : base(http, cache, clock, failures, logger, lyricsTtl) { }

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0) return Unavailable();
        var response = await GetAsync(new("https://fetch-genius.samidy.workers.dev/?title=" + Uri.EscapeDataString(request.TrackTitle.Trim()) +
            "&artist=" + Uri.EscapeDataString(string.Join(", ", request.ArtistNames))), token);
        if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
        if (string.IsNullOrWhiteSpace(response.Content)) return Unavailable();
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return ContractFailure();
        if (root.TryGetProperty("title", out var title) || root.TryGetProperty("artist", out _))
        {
            if (title.ValueKind != JsonValueKind.String || !root.TryGetProperty("artist", out var artist) ||
                artist.ValueKind != JsonValueKind.String || !MatchesIdentity(request, title.GetString(), artist.GetString())) return Unavailable();
        }
        if (!root.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind == JsonValueKind.Null) return Unavailable();
        if (lyrics.ValueKind != JsonValueKind.String) return ContractFailure();
        var lines = lyrics.GetString()!.Replace("\r", "").Split('\n').Select(line => line.Trim()).ToList();
        while (lines.Count > 0 && (lines[^1].Length == 0 || Footer().IsMatch(lines[^1]))) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        if (lines.Count > 0) lines[^1] = EmbedSuffix().Replace(lines[^1], "").TrimEnd();
        var text = string.Join('\n', lines).TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? Unavailable() : ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Available, ProviderId, ProviderLyricsFormat.PlainText, text, "sha256:" + Hash(text)));
    }

    [GeneratedRegex(@"\d*Embed$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbedSuffix();

    [GeneratedRegex(@"^(?:\d*\s*Embed|\d+\s+Contributors?|Contributors?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Footer();
}
