using allstarr.Core.Playback;
using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(499, true)]
    [InlineData(500, false)]
    public async Task ClientApi_PlaybackCurrentSelectsOnlyTheActiveRequestWithinTheExistingSampleWindow(int newerCount, bool included)
    {
        Guid requestId = default;
        var reads = new PlaybackAuditReads();
        var deliveries = new Mock<IPlaybackDeliveryActivitySource>();
        deliveries.Setup(service => service.StreamFor(It.IsAny<Guid?>(), "phone", "ext-deezer-song-42"))
            .Returns(() => new PlaybackStreamSource("deezer", "42", null, "jellyfin", "primary", true,
                DateTimeOffset.UtcNow, "provider-priority")
            { PlaybackRequestId = requestId });
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<IPlaybackDeliveryActivitySource>();
            services.AddSingleton(deliveries.Object);
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        });
        using var client = CreateClientApiClient(factory);
        using var icons = new ProviderIconFiles();
        icons.Use(factory.Services);
        requestId = await SeedClientPlaybackAsync(factory, "valid");
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42");
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var active = await db.AuditEvents.SingleAsync(row => row.Id == requestId);
            for (var index = 0; index < newerCount; index++) db.AuditEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                ActorUserId = active.ActorUserId,
                Category = active.Category,
                SubjectType = active.SubjectType,
                SubjectId = "recent",
                CreatedAt = active.CreatedAt.AddMilliseconds(1),
                DetailsJson = "{}"
            });
            await db.SaveChangesAsync();
        }
        reads.RequestIds.Clear();
        using var request = ClientPlaybackRequest("/allstarr/v1/playback/current");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(included, result.RootElement.GetProperty("delivered").ValueKind != JsonValueKind.Null);
        Assert.Contains(requestId.ToString(), reads.RequestIds, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PlaybackAuditReads : DbCommandInterceptor
    {
        internal readonly List<string> RequestIds = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("audit_events", StringComparison.Ordinal))
                RequestIds.AddRange(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value?.ToString() ?? ""));
            return ValueTask.FromResult(result);
        }
    }
}
