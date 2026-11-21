using allstarr.Core.Caching;
using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class StoredLyricsJudgmentTests
{
    private static readonly ProtocolExecutionContext Context = new(ProtocolKind.Jellyfin, "fixture", "listener", null,
        "stored-judgments", DateTimeOffset.UtcNow.AddHours(1), default);

    [Fact]
    public async Task RememberedJudgmentsChooseExactlyWhatAFreshReadChooses()
    {
        var root = Path.Combine(Path.GetTempPath(), "stored-judgments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(audio, "fixture");
            await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[00:01.000]Kept words\n[02:00.000]Kept ending");
            var songs = new[]
            {
                new Song { Title = "Fixture", Artist = "Artist" },
                new Song { Title = "Fixture", Artist = "Artist", Duration = 10 },
                new Song { Title = "Fixture", Artist = "Artist", Duration = 240, PreciseDuration = TimeSpan.FromMilliseconds(240_400) }
            };
            var orders = new[]
            {
                new[] { "lrclib", "lrcred", "amll", "unison", "genius" },
                new[] { "genius", "unison", "amll", "lrcred", "lrclib" },
                new[] { "kept", "lrcred", "lrclib", "lyricsplus" }
            };
            var retained = new LyricsInfo { Source = "lyricsplus", SyncedLyrics = "[00:01.00]Retained\n[03:50.00]Retained end" };
            var retainedPlain = new LyricsInfo { Source = "lrcred", PlainLyrics = "Retained words" };
            var compared = 0;
            foreach (var world in Worlds())
                foreach (var order in orders)
                    foreach (var song in songs)
                        foreach (var recording in new long?[] { null, 120_000 })
                            foreach (var automatic in new LyricsInfo?[] { null, retained, retainedPlain })
                                foreach (var pin in Pins(world, order))
                                    foreach (var file in new[] { null, audio })
                                    {
                                        var memo = new LyricsJudgments();
                                        var expected = await Outcome(world, order, song, pin, file, recording, automatic, LyricsJudgments.Off);
                                        Assert.Equal(expected, await Outcome(world, order, song, pin, file, recording, automatic, memo));
                                        Assert.Equal(expected, await Outcome(world, order, song, pin, file, recording, automatic, memo));
                                        compared++;
                                    }
            Assert.True(compared > 1000);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ASecondIdenticalCheckReusesTheJudgmentAndAChangedPayloadIsJudgedAgain()
    {
        var cache = new PayloadCache(new()
        {
            ["lrcred"] = Result(ProviderLyricsFormat.LineTimed, "[00:01.00]First\n[00:08.00]Last"),
            ["genius"] = Result(ProviderLyricsFormat.PlainText, "Plain words"),
            ["lrclib"] = JsonSerializer.Serialize(new LyricsInfo { PlainLyrics = "Search words" })
        });
        var memo = new LyricsJudgments();
        var song = new Song { Title = "Fixture", Artist = "Artist", Duration = 10 };
        var first = await Read(cache, ["lrclib", "lrcred", "genius"], song, null, null, null, null, memo);
        Assert.Equal(new ItemDetailsLyrics(true, "line", "lrcred"), first);
        Assert.Equal(3, memo.Judged);
        Assert.Equal(0, memo.Reused);

        Assert.Equal(first, await Read(cache, ["lrclib", "lrcred", "genius"], song, null, null, null, null, memo));
        Assert.Equal(3, memo.Judged);
        Assert.Equal(3, memo.Reused);

        cache.Payloads["lrcred"] = Result(ProviderLyricsFormat.WordTimed, "[00:01.000]<00:01.000>First <00:01.500>words\n[00:08.000]Last");
        Assert.Equal(new ItemDetailsLyrics(true, "word", "lrcred"), await Read(cache, ["lrclib", "lrcred", "genius"], song, null, null, null, null, memo));
        Assert.Equal(4, memo.Judged);
        Assert.Equal(5, memo.Reused);

        await Read(cache, ["lrclib", "lrcred", "genius"], new Song { Title = "Fixture", Artist = "Artist", Duration = 200 }, null, null, null, null, memo);
        Assert.Equal(7, memo.Judged);
    }

    [Fact]
    public async Task LyricsReadsWithObserversNeverUseRememberedJudgments()
    {
        var cache = new PayloadCache(new() { ["lrcred"] = Result(ProviderLyricsFormat.LineTimed, "[00:01.00]First\n[00:08.00]Last") });
        var memo = new LyricsJudgments();
        var observed = new List<RichLyrics>();
        var policy = Policy(["lrcred"]);
        for (var attempt = 0; attempt < 2; attempt++)
            await StoredItemLyrics.ReadAsync(cache, new Song { Title = "Fixture", Artist = "Artist" }, null, null, null, policy,
                "https://lyrics.example.test", Context, [], [], default, observed.Add, judgments: memo);
        Assert.Equal(2, observed.Count);
        Assert.Equal(0, memo.Judged);
        Assert.Equal(0, memo.Reused);
    }

    [Fact]
    public void RememberedJudgmentsStayWithinTheirEntryBound()
    {
        var memo = new LyricsJudgments(2);
        var song = new Song { Title = "Fixture", Duration = 10 };
        foreach (var words in new[] { "One", "Two", "Three", "One" })
            Assert.NotNull(memo.Versions("lrcred", new LyricsInfo { PlainLyrics = words }, song, null));
        Assert.Equal(4, memo.Judged);
        Assert.Equal(0, memo.Reused);
        Assert.NotNull(memo.Versions("lrcred", new LyricsInfo { PlainLyrics = "One" }, song, null));
        Assert.Equal(1, memo.Reused);
    }

    [Fact]
    public void PayloadsThatDifferOnlyInBrokenTextAreNotConfused()
    {
        var memo = new LyricsJudgments();
        var song = new Song { Title = "Fixture", Duration = 10 };
        memo.Versions("lrcred", new LyricsInfo { PlainLyrics = "Words \ud800" }, song, null);
        memo.Versions("lrcred", new LyricsInfo { PlainLyrics = "Words �" }, song, null);
        Assert.Equal(0, memo.Reused);
    }

    private static IEnumerable<Dictionary<string, string>> Worlds()
    {
        var wordLines = new ProviderLyricsTimedLine[]
        {
            new("café 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
                [new("café ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), new("世界", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))]),
            new("last line", TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(4),
                [new("last ", TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(2)), new("line", TimeSpan.FromSeconds(112), TimeSpan.FromSeconds(2))])
        };
        var versions = new[]
        {
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "unison", ProviderLyricsFormat.PlainText, "Plain verse")
            { Metadata = new("plain", "Fixture", "Artist", null, 240_000) },
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "unison", ProviderLyricsFormat.LineTimed,
                "[00:01.00]Short\n[00:05.00]Lines") { Metadata = new(null, "Fixture", "Artist", null, 10_000) },
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "unison", ProviderLyricsFormat.LineTimed,
                "[00:01.00]Fitting\n[01:55.00]Lines") { Metadata = new("fits", "Fixture (Live)", "Artist", null, 120_000) }
        };
        var best = new LyricsInfo { PlainLyrics = "Best verse", Metadata = new("101", "Fixture", "Artist", null, 240_000) };
        best.Versions =
        [
            new() { PlainLyrics = "Best verse", Metadata = best.Metadata },
            new() { SyncedLyrics = "[00:01]<00:01>Chosen <00:02>verse\n[03:58]End", Metadata = new("102", "Fixture (Live)", "Artist", null, 241_000) },
            new() { PlainLyrics = "   ", Metadata = new("103", "Fixture", "Artist", null, null) }
        ];
        yield return new()
        {
            ["lrclib"] = JsonSerializer.Serialize(best),
            ["lrcred"] = Result(ProviderLyricsFormat.LineTimed, "[00:01.000]<00:01.000>Service <00:01.500>words\n[01:58.000]End"),
            ["amll"] = JsonSerializer.Serialize(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "amll",
                ProviderLyricsFormat.WordTimed, "[00:01.000]café 世界\n[01:50.000]last line", timedLines: wordLines)),
            ["unison"] = JsonSerializer.Serialize(versions[0] with { Versions = versions }),
            ["genius"] = Result(ProviderLyricsFormat.PlainText, "I've heard there's more")
        };
        yield return new()
        {
            ["lrclib"] = JsonSerializer.Serialize(new LyricsInfo { SyncedLyrics = "[00:01.00]Line one\n[00:09.00]Line two" }),
            ["lrcred"] = Result(ProviderLyricsFormat.PlainText, "Service words"),
            ["lyricsplus"] = JsonSerializer.Serialize(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lyricsplus",
                ProviderLyricsFormat.WordTimed, "[00:01.000]café 世界\n[01:50.000]last line", timedLines: wordLines))
        };
        yield return new() { ["lrclib"] = JsonSerializer.Serialize(new LyricsInfo { PlainLyrics = "  " }) };
    }

    private static IEnumerable<string?> Pins(Dictionary<string, string> world, IReadOnlyList<string> order) =>
        new string?[] { null, "kept", "lrclib", "lrclib:102", "lrclib:missing", "unison", "unison:fits", "amll", "genius" }
            .Where(pin => pin == null || order.Contains(pin.Split(':')[0]) && (pin == "kept" || world.ContainsKey(pin.Split(':')[0])));

    private static async Task<string> Outcome(Dictionary<string, string> world, IReadOnlyList<string> order, Song song, string? pin,
        string? audio, long? recording, LyricsInfo? automatic, LyricsJudgments judgments)
    {
        try
        {
            // Each read gets its own copies, as a real cache read does.
            var retained = automatic == null ? null : JsonSerializer.Deserialize<LyricsInfo>(JsonSerializer.Serialize(automatic));
            return (await Read(new PayloadCache(new(world)), order, song, pin, audio, recording, retained, judgments)).ToString();
        }
        catch (Exception error) { return error.GetType().Name; }
    }

    private static Task<ItemDetailsLyrics> Read(PayloadCache cache, IReadOnlyList<string> order, Song song, string? pin, string? audio,
        long? recording, LyricsInfo? automatic, LyricsJudgments judgments) =>
        StoredItemLyrics.ReadAsync(cache, song, null, pin, audio, Policy(order), "https://lyrics.example.test", Context, [], [], default,
            recordingDurationMs: recording, automaticLyrics: automatic, judgments: judgments);

    private static EffectiveProviderPolicySnapshot Policy(IReadOnlyList<string> order) =>
        new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, [.. order]),
            ImmutableHashSet<string>.Empty, "lossless", .07);

    private static string Result(ProviderLyricsFormat format, string content) =>
        JsonSerializer.Serialize(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "fixture", format, content));

    private sealed class PayloadCache(Dictionary<string, string> payloads) : IApplicationCache
    {
        public Dictionary<string, string> Payloads { get; } = payloads;
        public bool IsEnabled => true;

        public Task<string?> GetStringAsync(string key)
        {
            var parts = key.Split(':');
            var provider = key.StartsWith("lrclib:automatic:", StringComparison.Ordinal) ? "lrclib" : parts.Length > 2 ? parts[2] : "";
            return Task.FromResult(Payloads.GetValueOrDefault(provider));
        }

        public async Task<T?> GetAsync<T>(string key) where T : class =>
            await GetStringAsync(key) is { } value ? JsonSerializer.Deserialize<T>(value) : null;

        public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) => Task.FromResult(false);
        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class => Task.FromResult(false);
        public Task<bool> DeleteAsync(string key) => Task.FromResult(false);
        public Task<bool> ExistsAsync(string key) => Task.FromResult(false);
        public Task<int> DeleteByPatternAsync(string pattern) => Task.FromResult(0);
        public Task<int> PurgeAllAsync() => Task.FromResult(0);
    }
}
