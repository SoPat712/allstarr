using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using allstarr.Controllers;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using System.Net;
using System.Net.Http;

namespace allstarr.Tests;

public class ScrobblingAdminControllerTests
{
    private readonly Mock<IOptions<ScrobblingSettings>> _mockSettings;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<ScrobblingAdminController>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly ScrobblingAdminController _controller;

    public ScrobblingAdminControllerTests()
    {
        _mockSettings = new Mock<IOptions<ScrobblingSettings>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<ScrobblingAdminController>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var settings = new ScrobblingSettings
        {
            Enabled = true,
            LastFm = new LastFmSettings
            {
                Enabled = true,
                ApiKey = "cb3bdcd415fcb40cd572b137b2b255f5",
                SharedSecret = "3a08f9fad6ddc4c35b0dce0062cecb5e",
                SessionKey = "",
                Username = null,
                Password = null
            }
        };

        _mockSettings.Setup(s => s.Value).Returns(settings);

        _controller = new ScrobblingAdminController(
            _mockSettings.Object,
            _mockConfiguration.Object,
            _mockHttpClientFactory.Object,
            _mockLogger.Object,
            null! // AdminHelperService not needed for these tests
        );
    }

    [Fact]
    public void GetStatus_ReturnsCorrectConfiguration()
    {
        // Act
        var result = _controller.GetStatus() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        
        dynamic? status = result.Value;
        Assert.NotNull(status);
    }

    [Theory]
    [InlineData("", "password123")]
    [InlineData("username", "")]
    [InlineData(null, "password123")]
    [InlineData("username", null)]
    public async Task AuthenticateLastFm_MissingCredentials_ReturnsBadRequest(string? username, string? password)
    {
        // Arrange - set credentials in settings
        var settings = new ScrobblingSettings
        {
            Enabled = true,
            LastFm = new LastFmSettings
            {
                Enabled = true,
                ApiKey = "cb3bdcd415fcb40cd572b137b2b255f5",
                SharedSecret = "3a08f9fad6ddc4c35b0dce0062cecb5e",
                SessionKey = "",
                Username = username,
                Password = password
            }
        };
        _mockSettings.Setup(s => s.Value).Returns(settings);

        var controller = new ScrobblingAdminController(
            _mockSettings.Object,
            _mockConfiguration.Object,
            _mockHttpClientFactory.Object,
            _mockLogger.Object,
            null! // AdminHelperService not needed for this test
        );

        // Act
        var result = await controller.AuthenticateLastFm() as BadRequestObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public void DebugAuth_ValidCredentials_ReturnsDebugInfo()
    {
        // Arrange
        var request = new ScrobblingAdminController.AuthenticateRequest
        {
            Username = "testuser",
            Password = "testpass123"
        };

        // Act
        var result = _controller.DebugAuth(request) as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        
        dynamic? debugInfo = result.Value;
        Assert.NotNull(debugInfo);
    }

    [Theory]
    [InlineData("user!@#$%", "pass!@#$%")]
    [InlineData("user with spaces", "pass with spaces")]
    [InlineData("user\ttab", "pass\ttab")]
    [InlineData("user'quote", "pass\"doublequote")]
    [InlineData("user&ampersand", "pass&ampersand")]
    [InlineData("user*asterisk", "pass*asterisk")]
    [InlineData("user$dollar", "pass$dollar")]
    [InlineData("user(paren)", "pass)paren")]
    [InlineData("user[bracket]", "pass{bracket}")]
    public void DebugAuth_SpecialCharacters_HandlesCorrectly(string username, string password)
    {
        // Arrange
        var request = new ScrobblingAdminController.AuthenticateRequest
        {
            Username = username,
            Password = password
        };

        // Act
        var result = _controller.DebugAuth(request) as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Value);
        
        // Use reflection to access anonymous type properties
        var passwordLengthProp = result.Value.GetType().GetProperty("PasswordLength");
        Assert.NotNull(passwordLengthProp);
        var passwordLength = (int?)passwordLengthProp.GetValue(result.Value);
        Assert.Equal(password.Length, passwordLength);
    }

    [Theory]
    [InlineData("test!pass456")]
    [InlineData("p@ssw0rd!")]
    [InlineData("test&test")]
    [InlineData("my*password")]
    [InlineData("pass$word")]
    public void DebugAuth_PasswordsWithShellSpecialChars_CalculatesCorrectLength(string password)
    {
        // Arrange
        var request = new ScrobblingAdminController.AuthenticateRequest
        {
            Username = "testuser",
            Password = password
        };

        // Act
        var result = _controller.DebugAuth(request) as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Value);
        
        // Use reflection to access anonymous type properties
        var passwordLengthProp = result.Value.GetType().GetProperty("PasswordLength");
        Assert.NotNull(passwordLengthProp);
        var passwordLength = (int?)passwordLengthProp.GetValue(result.Value);
        Assert.Equal(password.Length, passwordLength);
    }
}
