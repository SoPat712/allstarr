using Xunit;
using allstarr.Services.Admin;

namespace allstarr.Tests;

/// <summary>
/// Tests for input validation and sanitization
/// Ensures user inputs are properly validated and don't cause security issues
/// </summary>
public class InputValidationTests
{
    [Theory]
    [InlineData("username", true)]
    [InlineData("user123", true)]
    [InlineData("user_name", true)]
    [InlineData("user-name", true)]
    [InlineData("user.name", true)]
    [InlineData("user@domain", true)]      // Email format
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("user\nname", false)]      // Newline not allowed
    [InlineData("user\tname", false)]      // Tab not allowed
    [InlineData("user;name", false)]       // Semicolon suspicious
    [InlineData("user|name", false)]       // Pipe suspicious
    [InlineData("user&name", false)]       // Ampersand suspicious
    public void IsValidUsername_VariousInputs_ValidatesCorrectly(string username, bool expected)
    {
        // Act
        var result = AdminHelperService.IsValidUsername(username);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("pass!@#$%", true)]
    [InlineData("pass&word", true)]
    [InlineData("pass*word", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("pass\nword", false)]     // Newline not allowed
    [InlineData("pass\0word", false)]     // Null byte not allowed
    public void IsValidPassword_VariousInputs_ValidatesCorrectly(string password, bool expected)
    {
        // Act
        var result = AdminHelperService.IsValidPassword(password);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://localhost", true)]
    [InlineData("https://example.com", true)]
    [InlineData("http://192.168.1.1:8080", true)]
    [InlineData("https://example.com/path", true)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com", false)]
    public void IsValidUrl_VariousInputs_ValidatesCorrectly(string url, bool expected)
    {
        // Act
        var result = AdminHelperService.IsValidUrl(url);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/path/to/file", true)]
    [InlineData("./relative/path", true)]
    [InlineData("../parent/path", true)]
    [InlineData("/path/with spaces/file", true)]
    [InlineData("", false)]
    [InlineData("/path/with\nnewline", false)]
    [InlineData("/path/with\0null", false)]
    [InlineData("/path;rm -rf /", false)]
    [InlineData("/path|cat /etc/passwd", false)]
    [InlineData("/path&background", false)]
    public void IsValidPath_VariousInputs_ValidatesCorrectly(string path, bool expected)
    {
        // Act
        var result = AdminHelperService.IsValidPath(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("Normal text", "Normal text")]
    [InlineData("Text with <tags>", "Text with &lt;tags&gt;")]
    [InlineData("Text & more", "Text &amp; more")]
    [InlineData("Text \"quoted\"", "Text &quot;quoted&quot;")]
    [InlineData("Text 'quoted'", "Text &#39;quoted&#39;")]
    public void SanitizeHtml_VariousInputs_EscapesCorrectly(string input, string expected)
    {
        // Act
        var result = AdminHelperService.SanitizeHtml(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("normal-string", "normal-string")]
    [InlineData("string with spaces", "string with spaces")]
    [InlineData("string\nwith\nnewlines", "stringwithnewlines")]
    [InlineData("string\twith\ttabs", "stringwithtabs")]
    [InlineData("string\rwith\rcarriage", "stringwithcarriage")]
    public void RemoveControlCharacters_VariousInputs_RemovesCorrectly(string input, string expected)
    {
        // Act
        var result = AdminHelperService.RemoveControlCharacters(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("verylongpassword", 8, "verylong...")]
    [InlineData("short", 8, "short")]
    [InlineData("exactlen", 8, "exactlen")]
    [InlineData("", 8, "")]
    public void TruncateForLogging_VariousInputs_TruncatesCorrectly(string input, int maxLength, string expected)
    {
        // Act
        var result = AdminHelperService.TruncateForLogging(input, maxLength);

        // Assert
        Assert.Equal(expected, result);
    }
}
