using Xunit;
using allstarr.Services.Admin;

namespace allstarr.Tests;

/// <summary>
/// Tests for environment variable parsing edge cases
/// Ensures Docker Compose .env file parsing works correctly with special characters
/// </summary>
public class EnvironmentVariableParsingTests
{
    [Theory]
    [InlineData("password", "password")]
    [InlineData("\"password\"", "password")]
    [InlineData("'password'", "'password'")]  // Single quotes are NOT stripped by our logic
    public void ParseEnvValue_QuotedValues_HandlesCorrectly(string envValue, string expected)
    {
        // Act
        var result = AdminHelperService.StripQuotes(envValue);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("test!pass", "test!pass")]
    [InlineData("test&pass", "test&pass")]
    [InlineData("test*pass", "test*pass")]
    [InlineData("test$pass", "test$pass")]
    [InlineData("test@pass", "test@pass")]
    [InlineData("test#pass", "test#pass")]
    [InlineData("test%pass", "test%pass")]
    [InlineData("test^pass", "test^pass")]
    [InlineData("test(pass)", "test(pass)")]
    [InlineData("test[pass]", "test[pass]")]
    [InlineData("test{pass}", "test{pass}")]
    [InlineData("test|pass", "test|pass")]
    [InlineData("test\\pass", "test\\pass")]
    [InlineData("test;pass", "test;pass")]
    [InlineData("test'pass", "test'pass")]
    [InlineData("test`pass", "test`pass")]
    [InlineData("test~pass", "test~pass")]
    [InlineData("test<pass>", "test<pass>")]
    [InlineData("test?pass", "test?pass")]
    public void ParseEnvValue_ShellSpecialChars_PreservesWhenQuoted(string password, string expected)
    {
        // When properly quoted in .env file, special chars should be preserved
        var quotedValue = $"\"{password}\"";

        // Act
        var result = AdminHelperService.StripQuotes(quotedValue);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("SCROBBLING_LASTFM_PASSWORD=test!pass", "SCROBBLING_LASTFM_PASSWORD", "test!pass")]
    [InlineData("SCROBBLING_LASTFM_PASSWORD=\"test!pass\"", "SCROBBLING_LASTFM_PASSWORD", "test!pass")]
    [InlineData("MUSICBRAINZ_PASSWORD=test&pass", "MUSICBRAINZ_PASSWORD", "test&pass")]
    [InlineData("MUSICBRAINZ_PASSWORD=\"test&pass\"", "MUSICBRAINZ_PASSWORD", "test&pass")]
    public void ParseEnvLine_VariousFormats_ParsesCorrectly(string line, string expectedKey, string expectedValue)
    {
        // Act
        var (key, value) = AdminHelperService.ParseEnvLine(line);

        // Assert
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("# Comment line")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseEnvLine_CommentsAndEmpty_SkipsCorrectly(string line)
    {
        // Act
        var result = AdminHelperService.ShouldSkipEnvLine(line);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("KEY=value")]
    [InlineData("KEY=\"value\"")]
    public void ParseEnvLine_ValidLines_DoesNotSkip(string line)
    {
        // Act
        var result = AdminHelperService.ShouldSkipEnvLine(line);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("KEY=value with spaces", "value with spaces")]
    [InlineData("KEY=\"value with spaces\"", "value with spaces")]
    [InlineData("KEY=  value with leading spaces  ", "value with leading spaces")]
    public void ParseEnvLine_Whitespace_HandlesCorrectly(string line, string expectedValue)
    {
        // Act
        var (_, value) = AdminHelperService.ParseEnvLine(line);

        // Assert
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("KEY=", "")]
    [InlineData("KEY=\"\"", "")]
    [InlineData("KEY=  ", "")]
    public void ParseEnvLine_EmptyValues_HandlesCorrectly(string line, string expectedValue)
    {
        // Act
        var (_, value) = AdminHelperService.ParseEnvLine(line);

        // Assert
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("KEY=value=with=equals", "value=with=equals")]
    [InlineData("KEY=\"value=with=equals\"", "value=with=equals")]
    public void ParseEnvLine_MultipleEquals_HandlesCorrectly(string line, string expectedValue)
    {
        // Act
        var (_, value) = AdminHelperService.ParseEnvLine(line);

        // Assert
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("KEY=café", "café")]
    [InlineData("KEY=\"日本語\"", "日本語")]
    [InlineData("KEY=🎵🎶", "🎵🎶")]
    public void ParseEnvLine_UnicodeCharacters_HandlesCorrectly(string line, string expectedValue)
    {
        // Act
        var (_, value) = AdminHelperService.ParseEnvLine(line);

        // Assert
        Assert.Equal(expectedValue, value);
    }
}
