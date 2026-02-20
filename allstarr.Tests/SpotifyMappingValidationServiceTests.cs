using Xunit;
using System;
using allstarr.Models.Spotify;

namespace allstarr.Tests;

/// <summary>
/// Tests for Spotify mapping validation logic.
/// Focuses on the NeedsValidation() method and validation rules.
/// </summary>
public class SpotifyMappingValidationTests
{

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
    public void SpotifyTrackMapping_NeedsValidation_LocalMapping_ExactlySevenDays()
    {
        // Arrange
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = "test",
            TargetType = "local",
            LocalId = "abc123",
            Source = "auto",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddDays(-7) // Exactly 7 days
        };

        // Act
        var needsValidation = mapping.NeedsValidation(isPlaylistSync: false);

        // Assert
        Assert.True(needsValidation); // Should validate at 7 days
    }

    [Fact]
    public void SpotifyTrackMapping_NeedsValidation_ManualMapping_FollowsSameRules()
    {
        // Arrange - Manual local mapping
        var manualLocal = new SpotifyTrackMapping
        {
            SpotifyId = "test1",
            TargetType = "local",
            LocalId = "abc123",
            Source = "manual",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddDays(-8)
        };

        // Arrange - Manual external mapping
        var manualExternal = new SpotifyTrackMapping
        {
            SpotifyId = "test2",
            TargetType = "external",
            ExternalProvider = "SquidWTF",
            ExternalId = "789",
            Source = "manual",
            CreatedAt = DateTime.UtcNow,
            LastValidatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        // Act & Assert
        Assert.True(manualLocal.NeedsValidation(false)); // Manual local follows 7-day rule
        Assert.True(manualExternal.NeedsValidation(true)); // Manual external validates on sync
        Assert.False(manualExternal.NeedsValidation(false)); // But not outside sync
    }
}
