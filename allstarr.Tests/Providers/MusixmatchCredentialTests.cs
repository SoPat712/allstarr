using System.Text;
using System.Text.Json;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class MusixmatchCredentialTests
{
    [Theory]
    [InlineData("fixture-desktop-token+&=private")]
    [InlineData(" legacy_token/with.punctuation~%2B= ")]
    [InlineData("\tlegacy-token\r\n")]
    public void Parse_AcceptsLegacyTokens(string value)
    {
        Assert.True(MusixmatchCredential.TryParse(value, out var credential));
        Assert.Equal(value.Trim(), credential.UserToken);
        Assert.Null(credential.AppVersion);
        Assert.Null(credential.AppBuild);
    }

    [Fact]
    public void Parse_ReadsDebugFieldsInAnyOrderAndDropsUnusedFields()
    {
        const string input = "Debug Info\r\n[AppBuild]: 2025120901\r\n[DeviceId]: private-device\r\n[UserToken]: private-fixture-token\r\n[Other]: ignored\r\n[AppVersion]: 10.1.1\r\n";
        Assert.True(MusixmatchCredential.TryParse(input, out var credential));
        Assert.Equal("private-fixture-token", credential.UserToken);
        Assert.Equal("10.1.1", credential.AppVersion);
        Assert.Equal("2025120901", credential.AppBuild);
        using var saved = JsonDocument.Parse(credential.ToSecretBytes());
        Assert.Equal(3, saved.RootElement.EnumerateObject().Count());
        Assert.False(saved.RootElement.TryGetProperty("deviceId", out _));
        Assert.DoesNotContain("private-device", Encoding.UTF8.GetString(credential.ToSecretBytes()));
    }

    [Fact]
    public void Parse_OptionalDebugFieldsMayBeAbsent()
    {
        Assert.True(MusixmatchCredential.TryParse("Debug Info\n\t[UserToken] private-fixture-token\t\n[Other] ignored", out var credential));
        Assert.Null(credential.AppVersion);
        Assert.Null(credential.AppBuild);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("arbitrary copied prose")]
    [InlineData("{}")]
    [InlineData("<garbage>")]
    [InlineData("!!!")]
    [InlineData("fixture-token\nInjected: value")]
    [InlineData("[AppVersion]: 10.1.1\n[DeviceId]: private-device")]
    [InlineData("[UserToken]: ")]
    [InlineData("[UserToken]: fixture-token\n[UserToken]: other-token")]
    [InlineData("[UserToken]: fixture-token\n[AppVersion]: 10.1.1\tInjected")]
    [InlineData("[UserToken]: fixture-token\n[AppBuild]: 123\u0000")]
    public void Parse_RejectsMalformedInput(string? value) =>
        Assert.False(MusixmatchCredential.TryParse(value, out _));

    [Fact]
    public void Read_AcceptsNormalizedEncryptedPayload()
    {
        var secret = JsonSerializer.SerializeToUtf8Bytes(new { userToken = "fixture-token", appVersion = "10.1.1", appBuild = "2025120901" });
        Assert.True(MusixmatchCredential.TryRead(secret, out var credential));
        Assert.Equal("fixture-token", credential.UserToken);
        Assert.Equal("10.1.1", credential.AppVersion);
        Assert.Equal("2025120901", credential.AppBuild);
    }

    [Fact]
    public void Read_PreservesLegacyJsonAndAcceptsDebugInfoInUserToken()
    {
        Assert.True(MusixmatchCredential.TryRead(Encoding.UTF8.GetBytes("{\"userToken\":\"fixture-token\"}"), out var legacy));
        Assert.Equal("fixture-token", legacy.UserToken);
        Assert.Null(legacy.AppVersion);
        var debug = JsonSerializer.SerializeToUtf8Bytes(new
        { userToken = "[AppVersion]: 10.1.1\n[UserToken]: fixture-token", unused = "discarded" });
        Assert.True(MusixmatchCredential.TryRead(debug, out var credential));
        Assert.Equal("10.1.1", credential.AppVersion);
        Assert.Equal("fixture-token", credential.UserToken);
    }

    [Fact]
    public void Parse_RejectsOverlongValues()
    {
        Assert.False(MusixmatchCredential.TryParse(new string('x', 4_001), out _));
        Assert.False(MusixmatchCredential.TryParse("[UserToken]: fixture-token\n[AppBuild]: " + new string('1', 129), out _));
    }

    [Theory]
    [InlineData("{\"userToken\":\"fixture-token\",\"appVersion\":\"10.1.1\\r\\nInjected: value\"}")]
    [InlineData("{\"userToken\":\"fixture-token\",\"appBuild\":42}")]
    [InlineData("{\"userToken\":\"fixture-token\",\"UserToken\":\"other-token\"}")]
    [InlineData("{\"appVersion\":\"10.1.1\"}")]
    [InlineData("{\"userToken\":42}")]
    [InlineData("{\"userToken\":")]
    [InlineData("[]")]
    public void Read_RejectsMalformedPayload(string value) =>
        Assert.False(MusixmatchCredential.TryRead(Encoding.UTF8.GetBytes(value), out _));
}
