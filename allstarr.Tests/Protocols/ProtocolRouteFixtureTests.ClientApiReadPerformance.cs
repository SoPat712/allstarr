using Moq;
using System.Data.Common;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_EmptyHistoryDoesNotResolveLibraryCopies()
    {
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/history");
        response.EnsureSuccessStatusCode();
        Assert.Equal("{\"plays\":[]}", await response.Content.ReadAsStringAsync());
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientApi_ListeningStatsWarmReadUsesOneHistoryStampQuery()
    {
        var reads = new StatsStampReads();
        using var factory = ClientMeFactory(configure: services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var first = await client.GetAsync("/allstarr/v1/me/stats?period=30d&timeZoneId=UTC");
        first.EnsureSuccessStatusCode();
        reads.Count = 0;
        using var second = await client.GetAsync("/allstarr/v1/me/stats?period=30d&timeZoneId=UTC");
        second.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, reads.Count);
    }

    [Fact]
    public async Task ClientApi_ItemLyricsDeadlineIncludesMetadataResolution()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        TimeSpan? remaining = null;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "deadline"))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(150));
                return Task.FromResult<Song?>(new() { Id = "ext-deezer-song-deadline", Title = "Fixture", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "deadline" });
            });
        var lyrics = new Mock<IProtocolLyricsResolver>();
        lyrics.Setup(service => service.GetRichLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources?>()))
            .Callback<ProtocolExecutionContext, Song, string, ProtocolLyricsSources?>((execution, _, _, _) => remaining = execution.Deadline - clock.GetUtcNow())
            .ReturnsAsync((RichLyrics?)null);
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<ItemLyricsReader>();
            services.AddScoped(provider => ActivatorUtilities.CreateInstance<ItemLyricsReader>(provider, clock));
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(lyrics.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-deadline/lyrics");
        await AssertClientApiError(response, 404, "not_found");
        Assert.Equal(TimeSpan.FromMilliseconds(2850), remaining);
    }

    private sealed class StatsStampReads : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("listening_events", StringComparison.Ordinal) &&
                command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Count);
            return ValueTask.FromResult(result);
        }
    }
}
