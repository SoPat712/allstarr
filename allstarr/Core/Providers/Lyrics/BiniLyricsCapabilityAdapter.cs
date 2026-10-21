using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class BiniLyricsCapabilityAdapter : MetadataLyricsCapabilityAdapter
{
    public const string HttpClientName = "BiniLyrics";
    private static readonly MetadataLyricsFailureCache Failures = new(TimeProvider.System);
    private readonly CloudflareChallengeSolver? _solver;
    public override string ProviderId => "binilyrics";

    public BiniLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null,
        ILogger<BiniLyricsCapabilityAdapter>? logger = null, CloudflareChallengeSolver? solver = null)
        : this(http, cache, TimeProvider.System, Failures, logger, lyricsTtl) { _solver = solver; }

    protected override async Task<TimeSpan> ResolveLookupBudgetAsync(CancellationToken token) =>
        await SolverTimeoutAsync(token) is { } timeout ? timeout * 2 + OverallLookupBudget : OverallLookupBudget;

    protected override async Task<TimeSpan> ResolveRequestTimeoutAsync(CancellationToken token) =>
        await SolverTimeoutAsync(token) is { } timeout ? timeout + PerRequestTimeout : PerRequestTimeout;

    private Task<TimeSpan?> SolverTimeoutAsync(CancellationToken token) =>
        _solver?.ConfiguredTimeoutAsync(token) ?? Task.FromResult<TimeSpan?>(null);

    internal BiniLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, TimeProvider clock,
        MetadataLyricsFailureCache failures, ILogger? logger = null, Func<TimeSpan>? lyricsTtl = null)
        : base(http, cache, clock, failures, logger, lyricsTtl) { }

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Isrc)) queries.Add("isrc=" + Uri.EscapeDataString(request.Isrc));
        if (!string.IsNullOrWhiteSpace(request.TrackTitle) && request.ArtistNames.Count > 0) queries.Add(MetadataQuery(request, "track"));
        foreach (var query in queries)
        {
            var response = await GetAsync(new("https://lyrics-api.binimum.org/?" + query), token);
            if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
            if (string.IsNullOrWhiteSpace(response.Content)) continue;
            using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array) return ContractFailure();
            if (results.GetArrayLength() == 0) continue;
            var first = results[0];
            if (first.ValueKind != JsonValueKind.Object) return ContractFailure();
            var title = String(first, "track_name");
            var artist = String(first, "artist_name");
            var isrc = String(first, "isrc");
            if (!string.IsNullOrWhiteSpace(request.Isrc) && !string.IsNullOrWhiteSpace(isrc) &&
                !string.Equals(request.Isrc.Trim(), isrc.Trim(), StringComparison.OrdinalIgnoreCase)) return Unavailable();
            if (!string.IsNullOrWhiteSpace(request.TrackTitle) && request.ArtistNames.Count > 0)
            {
                double? duration = null;
                if (first.TryGetProperty("duration", out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || seconds < 0) return ContractFailure();
                    duration = seconds;
                }
                if (!MatchesIdentity(request, title, artist, duration)) return Unavailable();
            }
            else if (string.IsNullOrWhiteSpace(isrc) || !string.Equals(request.Isrc?.Trim(), isrc.Trim(), StringComparison.OrdinalIgnoreCase)) return Unavailable();
            if (!Uri.TryCreate(String(first, "lyricsUrl"), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "https" || endpoint.Host != "lyrics-storage.binimum.org" || !endpoint.IsDefaultPort ||
                endpoint.UserInfo.Length != 0) return ContractFailure();
            var lyrics = await GetAsync(endpoint, token);
            return lyrics.Error != null ? ProviderOutcome<ProviderLyricsResult>.Failure(lyrics.Error)
                : string.IsNullOrWhiteSpace(lyrics.Content) ? Unavailable() : ParseTtml(lyrics.Content);
        }
        return Unavailable();
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
