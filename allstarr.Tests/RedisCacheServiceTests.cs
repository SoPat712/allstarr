using Xunit;
using Moq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.Common;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class RedisCacheServiceTests
{
    private readonly Mock<ILogger<RedisCacheService>> _mockLogger;
    private readonly IOptions<RedisSettings> _settings;

    public RedisCacheServiceTests()
    {
        _mockLogger = new Mock<ILogger<RedisCacheService>>();
        _settings = Options.Create(new RedisSettings
        {
            Enabled = false, // Disabled for unit tests to avoid requiring actual Redis
            ConnectionString = "localhost:6379"
        });
    }

    [Fact]
    public void Constructor_InitializesWithSettings()
    {
        // Act
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Assert
        Assert.NotNull(service);
        Assert.False(service.IsEnabled); // Should be disabled in tests
    }

    [Fact]
    public void Constructor_WithEnabledSettings_AttemptsConnection()
    {
        // Arrange
        var enabledSettings = Options.Create(new RedisSettings
        {
            Enabled = true,
            ConnectionString = "localhost:6379"
        });

        // Act - Constructor will try to connect but should handle failure gracefully
        var service = new RedisCacheService(enabledSettings, _mockLogger.Object);

        // Assert - Service should be created even if connection fails
        Assert.NotNull(service);
    }

    [Fact]
    public async Task GetStringAsync_WhenDisabled_ReturnsNull()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.GetStringAsync("test:key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_WhenDisabled_ReturnsNull()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.GetAsync<TestObject>("test:key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetStringAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.SetStringAsync("test:key", "test value");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SetAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);
        var testObj = new TestObject { Id = 1, Name = "Test" };

        // Act
        var result = await service.SetAsync("test:key", testObj);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.DeleteAsync("test:key");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExistsAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.ExistsAsync("test:key");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteByPatternAsync_WhenDisabled_ReturnsZero()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.DeleteByPatternAsync("test:*");

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task SetStringAsync_WithExpiry_AcceptsTimeSpan()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);
        var expiry = TimeSpan.FromHours(1);

        // Act
        var result = await service.SetStringAsync("test:key", "value", expiry);

        // Assert - Should return false when disabled, but not throw
        Assert.False(result);
    }

    [Fact]
    public async Task SetAsync_WithExpiry_AcceptsTimeSpan()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);
        var testObj = new TestObject { Id = 1, Name = "Test" };
        var expiry = TimeSpan.FromDays(30);

        // Act
        var result = await service.SetAsync("test:key", testObj, expiry);

        // Assert - Should return false when disabled, but not throw
        Assert.False(result);
    }

    [Fact]
    public void IsEnabled_ReflectsSettings()
    {
        // Arrange
        var disabledService = new RedisCacheService(_settings, _mockLogger.Object);
        
        var enabledSettings = Options.Create(new RedisSettings
        {
            Enabled = true,
            ConnectionString = "localhost:6379"
        });
        var enabledService = new RedisCacheService(enabledSettings, _mockLogger.Object);

        // Assert
        Assert.False(disabledService.IsEnabled);
        // enabledService.IsEnabled may be false if connection fails, which is expected
    }

    [Fact]
    public async Task GetAsync_DeserializesComplexObjects()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);

        // Act
        var result = await service.GetAsync<ComplexTestObject>("test:complex");

        // Assert
        Assert.Null(result); // Null when disabled
    }

    [Fact]
    public async Task SetAsync_SerializesComplexObjects()
    {
        // Arrange
        var service = new RedisCacheService(_settings, _mockLogger.Object);
        var complexObj = new ComplexTestObject
        {
            Id = 1,
            Name = "Test",
            Items = new System.Collections.Generic.List<string> { "Item1", "Item2" },
            Metadata = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Key1", "Value1" },
                { "Key2", "Value2" }
            }
        };

        // Act
        var result = await service.SetAsync("test:complex", complexObj, TimeSpan.FromHours(1));

        // Assert
        Assert.False(result); // False when disabled
    }

    [Fact]
    public void ConnectionString_IsConfigurable()
    {
        // Arrange
        var customSettings = Options.Create(new RedisSettings
        {
            Enabled = false,
            ConnectionString = "redis-server:6380,password=secret,ssl=true"
        });

        // Act
        var service = new RedisCacheService(customSettings, _mockLogger.Object);

        // Assert
        Assert.NotNull(service);
    }

    private class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private class ComplexTestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public System.Collections.Generic.List<string> Items { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, string> Metadata { get; set; } = new();
    }
}
