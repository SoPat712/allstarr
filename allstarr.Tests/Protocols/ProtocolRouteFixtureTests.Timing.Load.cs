using System.Globalization;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private async Task<byte[][]> ReadConcurrentTimingAsync(HttpClient client, string path)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetAsync(path)));
        try
        {
            foreach (var response in responses) response.EnsureSuccessStatusCode();
            var timings = responses.Select(response => Assert.Single(response.Headers.GetValues("Server-Timing")))
                .Select(header => header.Split(", ").Where(entry => entry.Contains(";dur=", StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Split(';')[0], entry => double.Parse(entry.Split(';')[1][4..], CultureInfo.InvariantCulture)))
                .ToArray();
            static double Median(IEnumerable<double> values)
            {
                var ordered = values.Order().ToArray();
                return (ordered[5] + ordered[6]) / 2;
            }
            var totals = timings.Select(timing => timing["total"]).Order().ToArray();
            var stages = string.Join(", ", timings.SelectMany(timing => timing.Keys).Distinct()
                .Where(stage => stage != "total").Select(stage => $"{stage}={Median(timings.Select(timing => timing.GetValueOrDefault(stage))):0.0}"));
            _lyricsSearchOutput.WriteLine($"Concurrent 12 {path}: total median={Median(totals):0.0} ms, range={totals[0]:0.0}–{totals[^1]:0.0} ms; stage medians ms: {stages}");
            return await Task.WhenAll(responses.Select(response => response.Content.ReadAsByteArrayAsync()));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
