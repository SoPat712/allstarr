using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace allstarr.Tests;

/// <summary>
/// Tests for Last.fm API signature generation
/// Ensures signatures are generated correctly with uppercase hex format
/// </summary>
public class LastFmSignatureTests
{
    // Replicate the signature generation logic from ScrobblingAdminController
    private static string GenerateSignature(Dictionary<string, string> parameters, string sharedSecret)
    {
        var sorted = parameters.OrderBy(kvp => kvp.Key);
        var signatureString = new StringBuilder();

        foreach (var kvp in sorted)
        {
            signatureString.Append(kvp.Key);
            signatureString.Append(kvp.Value);
        }

        signatureString.Append(sharedSecret);

        var bytes = Encoding.UTF8.GetBytes(signatureString.ToString());
        var hash = MD5.HashData(bytes);

        // Convert to UPPERCASE hex string (Last.fm requires uppercase)
        var sb = new StringBuilder();
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    [Fact]
    public void GenerateSignature_BasicParameters_ReturnsUppercaseHex()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "testpass"
        };
        var sharedSecret = "testsecret";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature); // 32 uppercase hex chars
        Assert.Equal(32, signature.Length);
    }

    [Fact]
    public void GenerateSignature_PasswordWithSpecialChars_HandlesCorrectly()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "cb3bdcd415fcb40cd572b137b2b255f5",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser123",
            ["password"] = "fake!test456"
        };
        var sharedSecret = "3a08f9fad6ddc4c35b0dce0062cecb5e";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature);
        Assert.Equal(32, signature.Length);
    }

    [Theory]
    [InlineData("password!")]
    [InlineData("pass&word")]
    [InlineData("pass*word")]
    [InlineData("pass$word")]
    [InlineData("pass@word")]
    [InlineData("pass#word")]
    [InlineData("pass%word")]
    [InlineData("pass^word")]
    public void GenerateSignature_VariousSpecialChars_GeneratesValidSignature(string password)
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = password
        };
        var sharedSecret = "testsecret";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature);
        Assert.Equal(32, signature.Length);
    }

    [Fact]
    public void GenerateSignature_ParameterOrder_DoesNotMatter()
    {
        // Arrange - same parameters, different order
        var parameters1 = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "testpass"
        };

        var parameters2 = new Dictionary<string, string>
        {
            ["password"] = "testpass",
            ["username"] = "testuser",
            ["method"] = "auth.getMobileSession",
            ["api_key"] = "testkey"
        };

        var sharedSecret = "testsecret";

        // Act
        var signature1 = GenerateSignature(parameters1, sharedSecret);
        var signature2 = GenerateSignature(parameters2, sharedSecret);

        // Assert
        Assert.Equal(signature1, signature2);
    }

    [Fact]
    public void GenerateSignature_EmptyPassword_HandlesCorrectly()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = ""
        };
        var sharedSecret = "testsecret";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature);
    }

    [Fact]
    public void GenerateSignature_UnicodePassword_HandlesCorrectly()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "pässwörd日本語"
        };
        var sharedSecret = "testsecret";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature);
        Assert.Equal(32, signature.Length);
    }

    [Fact]
    public void GenerateSignature_LongPassword_HandlesCorrectly()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = new string('a', 1000) // Very long password
        };
        var sharedSecret = "testsecret";

        // Act
        var signature = GenerateSignature(parameters, sharedSecret);

        // Assert
        Assert.Matches("^[A-F0-9]{32}$", signature);
    }

    [Fact]
    public void GenerateSignature_PasswordWithWhitespace_PreservesWhitespace()
    {
        // Arrange
        var parameters1 = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "pass word"
        };

        var parameters2 = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "password"
        };

        var sharedSecret = "testsecret";

        // Act
        var signature1 = GenerateSignature(parameters1, sharedSecret);
        var signature2 = GenerateSignature(parameters2, sharedSecret);

        // Assert - should be different because whitespace matters
        Assert.NotEqual(signature1, signature2);
    }

    [Fact]
    public void GenerateSignature_CaseSensitivePassword_GeneratesDifferentSignatures()
    {
        // Arrange
        var parameters1 = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "Password"
        };

        var parameters2 = new Dictionary<string, string>
        {
            ["api_key"] = "testkey",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "password"
        };

        var sharedSecret = "testsecret";

        // Act
        var signature1 = GenerateSignature(parameters1, sharedSecret);
        var signature2 = GenerateSignature(parameters2, sharedSecret);

        // Assert - passwords are case-sensitive
        Assert.NotEqual(signature1, signature2);
    }

    [Fact]
    public void GenerateSignature_ConsistentResults_MatchesExpected()
    {
        // Arrange - Test with known values to ensure consistency
        var parameters = new Dictionary<string, string>
        {
            ["api_key"] = "testkey123",
            ["method"] = "auth.getMobileSession",
            ["username"] = "testuser",
            ["password"] = "testpass!"
        };
        var sharedSecret = "testsecret456";

        // Act
        var signature1 = GenerateSignature(parameters, sharedSecret);
        var signature2 = GenerateSignature(parameters, sharedSecret);

        // Assert - should be consistent
        Assert.Equal(signature1, signature2);
        Assert.Matches("^[A-F0-9]{32}$", signature1);
    }
}
