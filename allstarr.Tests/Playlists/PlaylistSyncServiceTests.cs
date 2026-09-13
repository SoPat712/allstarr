using allstarr.Models.Settings;
using allstarr.Services.Subsonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class PlaylistSyncServiceTests
{
    [Fact]
    public void ConstructionDoesNotRequireOptionalProviders()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"allstarr-playlists-{Guid.NewGuid():N}");
        try
        {
            var service = new PlaylistSyncService(
                [],
                [],
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:DownloadPath"] = directory
                }).Build(),
                Options.Create(new SubsonicSettings()),
                NullLogger<PlaylistSyncService>.Instance);

            Assert.NotNull(service);
            Assert.True(Directory.Exists(Path.Combine(directory, "playlists")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
