using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_ItemDetailsBatchesReviewQueriesAndReusesUnchangedResults()
    {
        var reads = new ItemDetailsReads();
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var ids = Enumerable.Range(0, 31).Select(index => "ext-deezer-song-batch-" + index).ToArray();
        foreach (var id in ids) await SeedClientDetailsAsync(factory, "listener", id[16..], personal: false);
        var expected = new List<JsonElement>();
        foreach (var id in ids)
        {
            using var single = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { id } });
            single.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await single.Content.ReadAsStringAsync());
            expected.Add(Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).Clone());
        }
        reads.Count = 0;
        reads.Delay = 3;
        var timer = Stopwatch.StartNew();
        using var first = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids });
        first.EnsureSuccessStatusCode();
        var coldMs = timer.ElapsedMilliseconds;
        Assert.Equal(JsonSerializer.Serialize(new { items = expected, missing = Array.Empty<string>() }), await first.Content.ReadAsStringAsync());
        var coldReads = reads.Count;
        reads.Count = 0;
        timer.Restart();
        using var second = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids });
        second.EnsureSuccessStatusCode();
        var warmMs = timer.ElapsedMilliseconds;
        var warmReads = reads.Count;
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var measurement = $"31 details with 3 ms per database read: cold {coldReads} reads / {coldMs} ms; warm {warmReads} reads / {warmMs} ms";
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(coldReads <= 80 && warmReads <= 50, measurement);
    }

    [Fact]
    public async Task ClientApi_ItemDetailsBatchReviewMatchesThePerItemPathWithFewerReads()
    {
        var reads = new ItemDetailsReads();
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        });
        using var client = CreateClientApiClient(factory);
        var recordings = new Dictionary<string, Guid?>();
        for (var index = 0; index < 31; index++) recordings["ext-deezer-song-review-" + index] =
            await SeedClientDetailsAsync(factory, "listener", "review-" + index, personal: false);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal, "review", DateTimeOffset.UtcNow.AddMinutes(1), default);
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ItemMatchReader>();
        var beforeHttp = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var memo = new TrackMatchLibraryMemo();
        reads.Count = 0;
        reads.Delay = 3;
        var timer = Stopwatch.StartNew();
        var expected = new List<ClientItemMatch?>();
        foreach (var id in recordings.Keys) expected.Add(await reader.ReadSummaryAsync(beforeHttp, execution, id, memo));
        var beforeMs = timer.ElapsedMilliseconds;
        Assert.All(expected, item => Assert.NotNull(item));
        var beforeReads = reads.Count;
        var afterHttp = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        memo = new TrackMatchLibraryMemo();
        reads.Count = 0;
        timer.Restart();
        await reader.PrepareSummariesAsync(afterHttp, execution, recordings, memo);
        var actual = new List<ClientItemMatch?>();
        foreach (var id in recordings.Keys) actual.Add(await reader.ReadSummaryAsync(afterHttp, execution, id, memo));
        var afterMs = timer.ElapsedMilliseconds;
        var afterReads = reads.Count;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        var measurement = $"31 match summaries, 3 ms per database read: per item {beforeReads} reads / {beforeMs} ms; batch {afterReads} reads / {afterMs} ms";
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(afterReads <= 25 && beforeReads >= afterReads * 10, measurement);
    }

    private sealed class ItemDetailsReads : DbCommandInterceptor
    {
        internal int Count;
        internal int Delay;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            if (Delay > 0) await Task.Delay(Delay, cancellationToken);
            return result;
        }
    }
}
