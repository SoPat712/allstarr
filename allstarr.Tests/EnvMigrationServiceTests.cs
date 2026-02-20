using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using allstarr.Services.Common;
using System.IO;

namespace allstarr.Tests;

public class EnvMigrationServiceTests
{
    private readonly Mock<ILogger<EnvMigrationService>> _mockLogger;
    private readonly string _testEnvPath;

    public EnvMigrationServiceTests()
    {
        _mockLogger = new Mock<ILogger<EnvMigrationService>>();
        _testEnvPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.env");
    }

    [Fact]
    public void MigrateEnvFile_RemovesQuotesFromPasswords()
    {
        // Arrange - passwords with quotes (old incorrect format)
        var envContent = @"SCROBBLING_LASTFM_USERNAME=testuser
SCROBBLING_LASTFM_PASSWORD=""test!pass123""
MUSICBRAINZ_PASSWORD=""fake&Pass*Word$123""
SOME_OTHER_VAR=value";

        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert - quotes should be removed
        var result = File.ReadAllText(_testEnvPath);
        Assert.Contains("SCROBBLING_LASTFM_PASSWORD=test!pass123", result);
        Assert.DoesNotContain("SCROBBLING_LASTFM_PASSWORD=\"test!pass123\"", result);
        Assert.Contains("MUSICBRAINZ_PASSWORD=fake&Pass*Word$123", result);
        Assert.DoesNotContain("MUSICBRAINZ_PASSWORD=\"fake&Pass*Word$123\"", result);
        Assert.Contains("SCROBBLING_LASTFM_USERNAME=testuser", result);
        Assert.Contains("SOME_OTHER_VAR=value", result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Fact]
    public void MigrateEnvFile_LeavesUnquotedPasswordsAlone()
    {
        // Arrange - passwords without quotes (correct format)
        var envContent = @"SCROBBLING_LASTFM_PASSWORD=already-unquoted!
MUSICBRAINZ_PASSWORD=also-unquoted&*$";

        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert - should remain unchanged
        var result = File.ReadAllText(_testEnvPath);
        Assert.Contains("SCROBBLING_LASTFM_PASSWORD=already-unquoted!", result);
        Assert.Contains("MUSICBRAINZ_PASSWORD=also-unquoted&*$", result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Fact]
    public void MigrateEnvFile_HandlesEmptyPasswords()
    {
        // Arrange
        var envContent = @"SCROBBLING_LASTFM_PASSWORD=
MUSICBRAINZ_PASSWORD=";

        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert
        var result = File.ReadAllText(_testEnvPath);
        Assert.Contains("SCROBBLING_LASTFM_PASSWORD=", result);
        Assert.DoesNotContain("SCROBBLING_LASTFM_PASSWORD=\"\"", result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Fact]
    public void MigrateEnvFile_PreservesComments()
    {
        // Arrange
        var envContent = @"# This is a comment
SCROBBLING_LASTFM_PASSWORD=fake!test123
# Another comment
MUSICBRAINZ_PASSWORD=test&pass*word";

        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert
        var result = File.ReadAllText(_testEnvPath);
        Assert.Contains("# This is a comment", result);
        Assert.Contains("# Another comment", result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Theory]
    [InlineData("DEEZER_ARL", "\"abc123def456!@#\"")]
    [InlineData("QOBUZ_USER_AUTH_TOKEN", "\"token&with*special$chars\"")]
    [InlineData("SCROBBLING_LASTFM_SESSION_KEY", "\"session!key@here\"")]
    [InlineData("SPOTIFY_API_SESSION_COOKIE", "\"cookie$value&here\"")]
    public void MigrateEnvFile_RemovesQuotesFromAllSensitiveKeys(string key, string quotedValue)
    {
        // Arrange - value with quotes (old incorrect format)
        var envContent = $"{key}={quotedValue}";
        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert - quotes should be removed
        var result = File.ReadAllText(_testEnvPath);
        var unquotedValue = quotedValue.Substring(1, quotedValue.Length - 2);
        Assert.Contains($"{key}={unquotedValue}", result);
        Assert.DoesNotContain(quotedValue, result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Fact]
    public void MigrateEnvFile_HandlesMultipleQuotedPasswords()
    {
        // Arrange - all with quotes (old incorrect format)
        var envContent = @"SCROBBLING_LASTFM_PASSWORD=""fakepass1!""
MUSICBRAINZ_PASSWORD=""testpass2&""
DEEZER_ARL=""fakearl3*""
QOBUZ_USER_AUTH_TOKEN=""testtoken4$""";

        File.WriteAllText(_testEnvPath, envContent);

        var service = new TestEnvMigrationService(_mockLogger.Object, _testEnvPath);

        // Act
        service.MigrateEnvFile();

        // Assert - all quotes should be removed
        var result = File.ReadAllText(_testEnvPath);
        Assert.Contains("SCROBBLING_LASTFM_PASSWORD=fakepass1!", result);
        Assert.DoesNotContain("SCROBBLING_LASTFM_PASSWORD=\"fakepass1!\"", result);
        Assert.Contains("MUSICBRAINZ_PASSWORD=testpass2&", result);
        Assert.DoesNotContain("MUSICBRAINZ_PASSWORD=\"testpass2&\"", result);
        Assert.Contains("DEEZER_ARL=fakearl3*", result);
        Assert.DoesNotContain("DEEZER_ARL=\"fakearl3*\"", result);
        Assert.Contains("QOBUZ_USER_AUTH_TOKEN=testtoken4$", result);
        Assert.DoesNotContain("QOBUZ_USER_AUTH_TOKEN=\"testtoken4$\"", result);

        // Cleanup
        File.Delete(_testEnvPath);
    }

    [Fact]
    public void MigrateEnvFile_NoFileExists_LogsWarning()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}.env");
        var service = new TestEnvMigrationService(_mockLogger.Object, nonExistentPath);

        // Act
        service.MigrateEnvFile();

        // Assert - should not throw, just log warning
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("No .env file found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // Helper class to allow testing with custom path
    private class TestEnvMigrationService : EnvMigrationService
    {
        private readonly string _customPath;

        public TestEnvMigrationService(ILogger<EnvMigrationService> logger, string customPath) 
            : base(logger)
        {
            _customPath = customPath;
            
            // Use reflection to set the private field
            var field = typeof(EnvMigrationService).GetField("_envFilePath", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(this, _customPath);
        }
    }
}
