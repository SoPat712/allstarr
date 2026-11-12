using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchStreamCompletionTimingPreservesProviderContent(bool neverAnswers)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var clock = new ManualTimeProvider();
        using var cutoff = new CancellationTokenSource(TimeSpan.FromMilliseconds(2750), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(guard.Token, cutoff.Token);
        var ids = new[] { "deezer", "qobuz", "monochrome", "spotiflac-tidal-web" };
        var releases = ids.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = Channel.CreateUnbounded<string>();
        var answered = Channel.CreateUnbounded<string>();
        var emitted = Channel.CreateUnbounded<(DateTimeOffset At, JsonNode Value)>();
        var capabilities = ids.Take(neverAnswers ? 4 : 3).Select((id, index) =>
        {
            var provider = DelayedSearchProvider(id, clock, TimeSpan.Zero);
            provider.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .Returns(async (ProviderExecutionContext context, ProviderMetadataSearchRequest _) =>
                {
                    started.Writer.TryWrite(id);
                    if (index == 3) await releases[index].Task;
                    else await releases[index].Task.WaitAsync(context.CancellationToken);
                    return ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new(id,
                        [new(new(id, ProviderResourceKind.Artist, "artist"), "Search artist " + index)]));
                });
            return provider.Object;
        }).ToArray();
        var snapshots = new RankedSearchSnapshots("Search artist", 10, ["artists"], ids[0], clock, (value, _) =>
        {
            emitted.Writer.TryWrite((clock.GetUtcNow(), JsonSerializer.SerializeToNode(value)!));
            return Task.CompletedTask;
        });
        var publishing = snapshots.RunAsync(guard.Token);
        var stream = BudgetGateway(clock, new TestMemoryApplicationCache(), capabilities)
            .StreamSearchAsync(Context(), "Search artist", 10, false, false, true, false, async (id, items, _, token) =>
            {
                await snapshots.AddAsync(id, items.Artists.Select(artist => new RankedSearchRow(new JsonObject
                { ["Id"] = artist.Id, ["Name"] = artist.Name, ["score"] = 95d }, "artists", Artist: artist)).ToArray(), token);
                answered.Writer.TryWrite(id);
            }, stop.Token);
        for (var index = 0; index < capabilities.Length; index++) await started.Reader.ReadAsync(guard.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        releases[0].SetResult();
        Assert.Equal(ids[0], await answered.Reader.ReadAsync(guard.Token));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(300));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        var first = await emitted.Reader.ReadAsync(guard.Token);
        releases[1].SetResult();
        Assert.Equal(ids[1], await answered.Reader.ReadAsync(guard.Token));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await emitted.Reader.ReadAsync(guard.Token);
        clock.Advance(TimeSpan.FromMilliseconds(850));
        releases[2].SetResult();
        Assert.Equal(ids[2], await answered.Reader.ReadAsync(guard.Token));
        var completeContent = await emitted.Reader.ReadAsync(guard.Token);
        if (neverAnswers) clock.Advance(TimeSpan.FromMilliseconds(1250));
        var late = await stream.WaitAsync(guard.Token);
        snapshots.Complete(late);
        await publishing.WaitAsync(guard.Token);
        var final = await emitted.Reader.ReadAsync(guard.Token);
        Assert.True(final.Value["done"]!.GetValue<bool>());
        foreach (var field in new[] { "providers", "top", "items", "sections" })
            Assert.True(JsonNode.DeepEquals(completeContent.Value[field], final.Value[field]), field);
        Assert.Equal(TimeSpan.FromMilliseconds(neverAnswers ? 2750 : 1500), final.At - DateTimeOffset.UnixEpoch);
        Assert.Equal(ids.Take(3), final.Value["providers"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(neverAnswers ? [ids[3]] : Array.Empty<string>(), late);
        Assert.Equal(late, final.Value["late"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(["Search artist 0", "Search artist 1", "Search artist 2"],
            final.Value["sections"]!["artists"]!.AsArray().Select(value => value!["Name"]!.GetValue<string>()));
        Assert.Equal(TimeSpan.FromMilliseconds(400), first.At - DateTimeOffset.UnixEpoch);
        releases[3].TrySetCanceled();
        searchOutput.WriteLine($"Fake providers: 100, 400, 1500 ms; never answers={neverAnswers}; first={(first.At - DateTimeOffset.UnixEpoch).TotalMilliseconds} ms; done={(final.At - DateTimeOffset.UnixEpoch).TotalMilliseconds} ms.");
    }
}
