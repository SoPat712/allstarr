using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DownloadsControllerCacheRetentionTests
{
    [Theory]
    [InlineData("tracked")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("no-last-use")]
    public async Task CachedExpiryUsesTrackedLastUseAndCurrentRetentionWithoutInventingUnknownDates(string identity)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-cache-retention-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "cache");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "track.flac");
            await File.WriteAllTextAsync(path, "fixture-audio");
            var lastUsed = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(path, lastUsed.AddDays(-3).UtcDateTime);
            var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.DownloadedSongMappings.Add(new DownloadedSongMappingEntity
                {
                    Id = Guid.NewGuid(),
                    ProviderId = "deezer",
                    ExternalId = "fixture",
                    LocalPath = path,
                    CacheIdentityJson = identity switch
                    {
                        "tracked" => JsonSerializer.Serialize(new { Device = "device", File = "file", Length = 13, Sha256 = "hash", LastUsed = lastUsed }),
                        "malformed" => "not-json",
                        "no-last-use" => "{}",
                        _ => null
                    }
                });
                await db.SaveChangesAsync();
            }
            var settings = new SubsonicSettings { StorageMode = StorageMode.Cache, CacheDurationHours = 72 };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = root,
                ["Library:CacheDurationHours"] = "24"
            }).Build();
            using var services = new ServiceCollection()
                .AddSingleton<IOptions<SubsonicSettings>>(Options.Create(settings)).BuildServiceProvider();
            var controller = ActivatorUtilities.CreateInstance<DownloadsController>(services,
                NullLogger<DownloadsController>.Instance, configuration, factory);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            foreach (var hours in new[] { 72, 48 })
            {
                settings.CacheDurationHours = hours;
                var result = Assert.IsType<OkObjectResult>(await controller.GetDownloads("cache"));
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var file = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
                if (identity == "tracked")
                {
                    Assert.Equal(lastUsed.AddHours(hours).UtcDateTime, file.GetProperty("expiresAt").GetDateTime());
                    Assert.Equal(lastUsed.UtcDateTime, file.GetProperty("lastAccessedAt").GetDateTime());
                }
                else Assert.Equal(JsonValueKind.Null, file.GetProperty("expiresAt").ValueKind);
                Assert.True(File.Exists(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
