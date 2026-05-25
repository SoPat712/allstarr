using System.Text.Json;

namespace allstarr.Services.SquidWTF;

public static class SquidWtfEndpointDiscovery
{
    public static readonly IReadOnlyList<string> SourceUrls = new[]
    {
        "https://tidal-uptime.geeked.wtf/",
        "https://tidal-uptime.jiffy-puffs-1j.workers.dev/",
        "https://tidal-uptime.props-76styles.workers.dev/"
    };

    public static async Task<SquidWtfEndpointCatalog> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var feeds = new List<EndpointFeed>();

        foreach (var sourceUrl in SourceUrls)
        {
            try
            {
                Console.WriteLine($"Loading SquidWTF uptime feed: {sourceUrl}");
                var json = await httpClient.GetStringAsync(sourceUrl, cancellationToken);
                var feed = ParseFeed(json);
                feeds.Add(feed);
                Console.WriteLine(
                    $"Loaded SquidWTF uptime feed {sourceUrl}: api={feed.ApiUrls.Count}, streaming={feed.StreamingUrls.Count}, down={feed.DownUrls.Count}, lastUpdated={feed.LastUpdated:O}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to load SquidWTF endpoint feed from {sourceUrl}: {ex.Message}");
            }
        }

        if (feeds.Count == 0)
        {
            Console.WriteLine(
                "⚠️ No SquidWTF uptime feeds could be loaded. Starting with SquidWTF external features unavailable; local Jellyfin content will still work.");
            return new SquidWtfEndpointCatalog(new List<string>(), new List<string>());
        }

        var orderedFeeds = feeds
            .OrderByDescending(f => f.LastUpdated)
            .ToList();

        var downUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in orderedFeeds)
        {
            foreach (var downUrl in feed.DownUrls)
            {
                downUrls.Add(downUrl);
            }
        }

        var apiUrls = MergeDistinctUrls(orderedFeeds.Select(f => f.ApiUrls))
            .Where(url => !downUrls.Contains(url))
            .ToList();

        var streamingUrls = MergeDistinctUrls(orderedFeeds.Select(f => f.StreamingUrls))
            .Where(url => !downUrls.Contains(url))
            .ToList();

        if (apiUrls.Count == 0)
        {
            Console.WriteLine("⚠️ SquidWTF uptime feeds returned zero API endpoints.");
        }

        if (streamingUrls.Count == 0)
        {
            Console.WriteLine("⚠️ SquidWTF uptime feeds returned zero streaming endpoints.");
        }

        Console.WriteLine($"Loaded SquidWTF endpoints from uptime feeds: api={apiUrls.Count}, streaming={streamingUrls.Count}");

        return new SquidWtfEndpointCatalog(apiUrls, streamingUrls);
    }

    private static EndpointFeed ParseFeed(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        DateTimeOffset lastUpdated = DateTimeOffset.MinValue;
        if (root.TryGetProperty("lastUpdated", out var lastUpdatedElement) &&
            lastUpdatedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(lastUpdatedElement.GetString(), out var parsedLastUpdated))
        {
            lastUpdated = parsedLastUpdated;
        }

        var apiUrls = ParseUrlList(root, "api");
        var streamingUrls = ParseUrlList(root, "streaming");
        var downUrls = ParseUrlList(root, "down");

        return new EndpointFeed(lastUpdated, apiUrls, streamingUrls, downUrls);
    }

    private static List<string> ParseUrlList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var urls = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            string? rawUrl = null;

            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("url", out var urlElement))
            {
                rawUrl = urlElement.GetString();
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                rawUrl = item.GetString();
            }

            if (TryNormalizeUrl(rawUrl, out var normalizedUrl))
            {
                urls.Add(normalizedUrl);
            }
        }

        return urls;
    }

    private static IEnumerable<string> MergeDistinctUrls(IEnumerable<List<string>> lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in lists)
        {
            foreach (var url in list)
            {
                if (seen.Add(url))
                {
                    yield return url;
                }
            }
        }
    }

    private static bool TryNormalizeUrl(string? rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = trimmed.TrimEnd('/');
        return true;
    }

    private sealed record EndpointFeed(
        DateTimeOffset LastUpdated,
        List<string> ApiUrls,
        List<string> StreamingUrls,
        List<string> DownUrls);
}
