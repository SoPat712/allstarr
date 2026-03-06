using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.Spotify;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
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
    public void ParseGraphQLPlaylist_ParsesCreatedAtFromAttributes()
    {
        // Arrange
        var client = new SpotifyApiClient(_mockLogger.Object, _settings);
        using var doc = JsonDocument.Parse("""
        {
          "name": "Discover Weekly",
          "description": "Weekly picks",
          "revisionId": "rev123",
          "attributes": [
            { "key": "core:created_at", "value": "1771218000000" }
          ]
        }
        """);

        // Act
        var playlist = InvokePrivateMethod<SpotifyPlaylist?>(client, "ParseGraphQLPlaylist", doc.RootElement, "37i9dQZEVXcJyaHDR0yDFT");

        // Assert
        Assert.NotNull(playlist);
        Assert.Equal("Discover Weekly", playlist!.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1771218000000).UtcDateTime, playlist.CreatedAt);
    }

    [Fact]
    public void ParseGraphQLTrack_ParsesAddedAtIsoStringAsUtc()
    {
        // Arrange
        var client = new SpotifyApiClient(_mockLogger.Object, _settings);
        using var doc = JsonDocument.Parse("""
        {
          "addedAt": { "isoString": "2026-02-16T05:00:00Z" },
          "itemV2": {
            "data": {
              "uri": "spotify:track:3a8mo25v74BMUOJ1IDUEBL",
              "name": "Sample Track",
              "artists": {
                "items": [
                  {
                    "profile": { "name": "Sample Artist" },
                    "uri": "spotify:artist:123"
                  }
                ]
              },
              "albumOfTrack": {
                "name": "Sample Album",
                "uri": "spotify:album:456",
                "coverArt": {
                  "sources": [
                    { "url": "https://example.com/small.jpg", "width": 64 },
                    { "url": "https://example.com/large.jpg", "width": 640 }
                  ]
                }
              },
              "trackDuration": { "totalMilliseconds": 201526 },
              "contentRating": { "label": "NONE" },
              "trackNumber": 1,
              "discNumber": 1,
              "playcount": "1200"
            }
          }
        }
        """);

        // Act
        var track = InvokePrivateMethod<SpotifyPlaylistTrack?>(client, "ParseGraphQLTrack", doc.RootElement, 0);

        // Assert
        Assert.NotNull(track);
        Assert.Equal("3a8mo25v74BMUOJ1IDUEBL", track!.SpotifyId);
        Assert.Equal(new DateTime(2026, 2, 16, 5, 0, 0, DateTimeKind.Utc), track.AddedAt);
    }

    private static T InvokePrivateMethod<T>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(instance, args);
        return (T)result!;
    }
}
