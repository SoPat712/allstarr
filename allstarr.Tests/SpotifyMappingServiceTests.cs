using Xunit;
using Moq;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.Spotify;
using allstarr.Services.Common;
using allstarr.Models.Spotify;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class SpotifyMappingServiceTests
{
    private readonly Mock<ILogger<RedisCacheService>> _mockCacheLogger;
    private readonly Mock<ILogger<SpotifyMappingService>> _mockLogger;
    private readonly RedisCacheService _cache;
    private readonly SpotifyMappingService _service;

    public SpotifyMappingServiceTests()
    {
        _mockCacheLogger = new Mock<ILogger<RedisCacheService>>();
        _mockLogger = new Mock<ILogger<SpotifyMappingService>>();
        
        // Use disabled Redis for tests
        var redisSettings = Options.Create(new RedisSettings
        {
            Enabled = false,
            ConnectionString = "localhost:6379"
        });
        
        _cache = new RedisCacheService(redisSettings, _mockCacheLogger.Object);
        _service = new SpotifyMappingService(_cache, _mockLogger.Object);
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_LocalMapping_WithinSevenDays()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "local",
            LocalId = "abc123",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddDays(-3) // 3 days ago
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: false);

        // Assert
        Assert.False(needsValidation); // Should not need validation yet
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_LocalMapping_AfterSevenDays()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "local",
            LocalId = "abc123",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddDays(-8) // 8 days ago
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: false);

        // Assert
        Assert.True(needsValidation); // Should need validation
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_ExternalMapping_OnPlaylistSync()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "external",
            ExternalProvider = "SquidWTF",
            ExternalId = "789",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddMinutes(-5) // Just validated
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: true);

        // Assert
        Assert.True(needsValidation); // Should validate on every sync
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_ExternalMapping_NotOnPlaylistSync()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "external",
            ExternalProvider = "SquidWTF",
            ExternalId = "789",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: false);

        // Assert
        Assert.False(needsValidation); // Should not validate if not playlist sync
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_NeverValidated()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "local",
            LocalId = "abc123",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = null // Never validated
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: false);

        // Assert
        Assert.True(needsValidation); // Should always validate if never validated
    }

    [Fact]
    public async Task SaveMappingAsync_RejectsInvalidLocalMapping()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "3n3Ppam7vgaVa1iaRUc9Lp",
            TargetType = "local",
            LocalId = null, // Invalid - no LocalId
            Source = "auto",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.SaveMappingAsync(mapping);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SaveMappingAsync_RejectsInvalidExternalMapping()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "3n3Ppam7vgaVa1iaRUc9Lp",
            TargetType = "external",
            ExternalProvider = "SquidWTF",
            ExternalId = null, // Invalid - no ExternalId
            Source = "auto",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.SaveMappingAsync(mapping);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SaveMappingAsync_RejectsEmptySpotifyId()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "", // Invalid - empty
            TargetType = "local",
            LocalId = "abc123",
            Source = "auto",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.SaveMappingAsync(mapping);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetMappingAsync_ReturnsNullWhenNotFound()
    {
        // Arrange
        var spotifyId = "nonexistent";

        // Act
        var result = await _service.GetMappingAsync(spotifyId);

        // Assert
        Assert.Null(result); // Redis is disabled, so nothing will be found
    }
}
