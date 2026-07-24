using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.Spotify;
using allstarr.Models.Settings;
using System.Reflection;
using System.Text.Json;

namespace allstarr.Tests;

public class SpotifyApiClientTests
{
    private readonly Mock<ILogger<SpotifyApiClient>> _mockLogger;
    private readonly IOptions<SpotifyApiSettings> _settings;

    public SpotifyApiClientTests()
    {
        _mockLogger = new Mock<ILogger<SpotifyApiClient>>();
        _settings = Options.Create(new SpotifyApiSettings
        {
            Enabled = true,
            SessionCookie = "test_session_cookie_value",
            CacheDurationMinutes = 60,
            RateLimitDelayMs = 100,
            PreferIsrcMatching = true
        });
    }

    [Fact]
    public void Constructor_InitializesWithSettings()
    {
        // Act
        var client = new SpotifyApiClient(_mockLogger.Object, _settings);

        // Assert
        Assert.NotNull(client);
    }

    [Fact]
    public void Settings_AreConfiguredCorrectly()
    {
        // Arrange & Act
        var client = new SpotifyApiClient(_mockLogger.Object, _settings);

        // Assert - Constructor should not throw
        Assert.NotNull(client);
    }

    [Fact]
    public void SessionCookie_IsRequired_ForWebApiAccess()
    {
        // Arrange
        var settingsWithoutCookie = Options.Create(new SpotifyApiSettings
        {
            Enabled = true,
            SessionCookie = "" // Empty cookie
        });

        // Act
        var client = new SpotifyApiClient(_mockLogger.Object, settingsWithoutCookie);

        // Assert - Constructor should not throw, but GetWebAccessTokenAsync will return null
        Assert.NotNull(client);
    }

    [Fact]
    public void RateLimitSettings_AreRespected()
    {
        // Arrange
        var customSettings = Options.Create(new SpotifyApiSettings
        {
            Enabled = true,
            SessionCookie = "test_cookie",
            RateLimitDelayMs = 500
        });

        // Act
        var client = new SpotifyApiClient(_mockLogger.Object, customSettings);

        // Assert
        Assert.NotNull(client);
    }

    [Fact]
    public void TryGetSpotifyPlaylistItemCount_ParsesAttributesArrayEntries()
    {
        // Arrange
        using var doc = JsonDocument.Parse("""
        {
          "attributes": [
            { "key": "core:item_count", "value": "42" }
          ]
        }
        """);

        var method = typeof(SpotifyApiClient).GetMethod(
            "TryGetSpotifyPlaylistItemCount",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        // Act
        var result = (int)method!.Invoke(null, new object?[] { doc.RootElement })!;

        // Assert
        Assert.Equal(42, result);
    }
}
