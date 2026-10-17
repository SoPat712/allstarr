using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.Matching;

public interface IMatchLibraryClient
{
    Task<IReadOnlyList<LibraryTrackIndexInput>> SearchAsync(ProtocolExecutionContext context, string libraryId, string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryTrackIndexInput>> ArtistSongsAsync(ProtocolExecutionContext context, string libraryId, string artist, CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryTrackIndexInput>> IsrcAsync(ProtocolExecutionContext context, string libraryId, string isrc, CancellationToken cancellationToken);
}

public sealed class MatchLibrarySearch(IMatchLibraryClient client, TimeSpan? budget = null)
{
    public async Task<IReadOnlyList<LibraryTrackIndexInput>> GatherAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<string> libraryIds,
        string query,
        ExternalTrackMatchSnapshot? source,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
        deadline.CancelAfter(budget ?? TimeSpan.FromSeconds(4));
        var title = QueryTitle(query, source);
        var queries = QueryVariants(query, source);
        var work = new List<Func<CancellationToken, Task<IReadOnlyList<LibraryTrackIndexInput>>>>();
        foreach (var library in libraryIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var term in queries)
                work.Add(token => client.SearchAsync(context, library, term, token));
            foreach (var artist in FuzzyMatcher.SplitCredits(source?.Artist).Take(2))
                work.Add(async token => (await client.ArtistSongsAsync(context, library, artist, token))
                    .Where(item => SimilarTitle(title, item.Title)).ToArray());
            if (!string.IsNullOrWhiteSpace(source?.Isrc))
                work.Add(token => client.IsrcAsync(context, library, source.Isrc, token));
        }
        var found = new ConcurrentQueue<LibraryTrackIndexInput>();
        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = deadline.Token },
                async (strategy, token) =>
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        foreach (var item in await strategy(attempt.Token).WaitAsync(attempt.Token)) found.Enqueue(item);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or UnauthorizedAccessException)
                    {
                    }
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
        context.CancellationToken.ThrowIfCancellationRequested();
        return found.DistinctBy(item => item.BackendItemId, StringComparer.Ordinal).ToArray();
    }

    public static string QueryTitle(string query, ExternalTrackMatchSnapshot? source)
    {
        var title = query.Trim();
        foreach (var artist in FuzzyMatcher.SplitCredits(source?.Artist).OrderByDescending(item => item.Length))
        {
            if (title.EndsWith(" " + artist, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^(artist.Length + 1)].Trim();
                break;
            }
        }
        return title.Length >= 2 ? title : query.Trim();
    }

    public static IReadOnlyList<string> QueryVariants(string query, ExternalTrackMatchSnapshot? source)
    {
        var title = QueryTitle(query, source);
        var stripped = FuzzyMatcher.SearchQuery(title);
        var normalized = NormalizeTitle(title);
        return new[] { query.Trim(), title, stripped, normalized, Regex.Replace(normalized, @"\band\b", "&", RegexOptions.IgnoreCase) }
            .Where(item => item.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizeTitle(string title)
    {
        var decomposed = FuzzyMatcher.SearchQuery(title).Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var plain = new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(Regex.Replace(plain, @"[^\p{L}\p{N}\s]", " "), @"\s+", " ").Trim();
    }

    public static bool SimilarTitle(string query, string title) =>
        FuzzyMatcher.CalculateSimilarity(NormalizeTitle(query), NormalizeTitle(title)) >= 75;
}
