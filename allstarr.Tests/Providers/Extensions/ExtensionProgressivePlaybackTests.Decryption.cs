using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    [Theory]
    [InlineData("sinf")]
    [InlineData("enca")]
    [InlineData("encv")]
    [InlineData("pssh")]
    [InlineData("tenc")]
    public void Playback_EncryptedMp4MetadataCannotPublishBeforeCompletion(string marker)
    {
        var transfer = new ExtensionProgressiveTransfer(TimeProvider.System, true);
        transfer.Begin("input", "input", prefix.Length, "audio/mp4");
        var bytes = new byte[prefix.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, 8);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 16);
        "moov"u8.CopyTo(bytes.AsSpan(12));
        System.Text.Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 20);
        transfer.Append(bytes);
        Assert.False(transfer.Published);
        transfer.Cleanup();
    }

    [Fact]
    public async Task Playback_EncryptedAudioWaitsForVerificationThenServesRangesFromSharedArtifact()
    {
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, "fixture.m4a");
        await ExtensionDownloadDecryptionTests.CreateFixtureAsync(fixture);
        await using (var append = new FileStream(fixture, FileMode.Append))
        {
            var padding = new byte[300 * 1024];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(padding, padding.Length);
            "free"u8.CopyTo(padding.AsSpan(4));
            await append.WriteAsync(padding);
        }
        var bytes = await File.ReadAllBytesAsync(fixture);
        Assert.True(bytes.Length > 256 * 1024);
        var processing = "return Object.assign({success:true,path:path}," + ExtensionDownloadDecryptionTests.Descriptor() + ");";
        var (adapter, context, track) = Setup(false, processing, "audio/mp4", extension: ".m4a");
        sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(30), ExtensionDownloadDecryptionTests.MediaRunner);
        var first = adapter.GetStreamLeaseAsync(context, new(track));
        var second = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(bytes[..^4]);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        source.Release(bytes[^4..]);
        source.Complete();
        var leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        var lease = leases[0].RequireValue();
        Assert.Equal("m4a", lease.Media.Container);
        Assert.Equal("aac", lease.Media.Codec);
        using var fullRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var full = await lease.ProtectedResponseFactory!(fullRequest, default);
        var decrypted = await full.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(bytes, decrypted);
        var rangeLease = leases[1].RequireValue();
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, rangeLease.ProtectedSourceUri);
        rangeRequest.Headers.Range = new RangeHeaderValue(4, 15);
        using var range = await rangeLease.ProtectedResponseFactory!(rangeRequest, default);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(decrypted[4..16], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
        var message = Assert.Single(logger.Messages, message => message.Contains(" decryption ", StringComparison.Ordinal));
        Assert.Contains("success", message);
        Assert.DoesNotContain(ExtensionDownloadDecryptionTests.Key, message);
        Assert.DoesNotContain(root, message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("ffeeddccbbaa99887766554433221100")]
    public async Task Playback_EncryptedAudioFailureLeavesNoArtifactAndAllowsFallback(string key)
    {
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, "fixture.m4a");
        await ExtensionDownloadDecryptionTests.CreateFixtureAsync(fixture);
        var bytes = await File.ReadAllBytesAsync(fixture);
        var processing = "return Object.assign({success:true,path:path}," + ExtensionDownloadDecryptionTests.Descriptor(key) + ");";
        var (adapter, context, track) = Setup(false, processing, "audio/mp4", extension: ".m4a");
        sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(30), ExtensionDownloadDecryptionTests.MediaRunner);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(bytes);
        source.Complete();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        Assert.Single(logger.Messages, message => message.Contains(" decryption ", StringComparison.Ordinal));
    }
}
