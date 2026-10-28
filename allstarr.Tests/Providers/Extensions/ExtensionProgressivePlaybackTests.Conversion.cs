using allstarr.Core.Music;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    [MediaToolsFact]
    public async Task UnknownLengthProgressiveOriginalAndConversionFinishWithOneProcessSlot()
    {
        Directory.CreateDirectory(root);
        var wave = PlaybackAudioFixtures.Wave();
        System.Security.Cryptography.RandomNumberGenerator.Fill(wave.AsSpan(44));
        var input = Path.Combine(root, "input.wav");
        var path = Path.Combine(root, "input.flac");
        await File.WriteAllBytesAsync(input, wave);
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-threads", "1", "-i", input, "-c:a", "flac", "-threads", "1", path]);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length > 262144);
        var gate = new MediaConversionGate(new MediaCapacityLimits(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        var verifier = new ProviderAudioVerifier(gate);
        var (adapter, context, track) = Setup(false, maximumBytes: 4 * 1024 * 1024, audioVerifier: verifier);
        var opening = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(bytes[..262144]);
        var lease = (await opening.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Null(response.Content.Headers.ContentLength);
        var verified = (await verifier.VerifyPrefixAsync(response, lease.Media, 8, default)).RequireValue();
        var plan = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(new() { MaxBitrate = 128000 }, verified.Media));
        await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, plan, gate, default, durationSeconds: 8);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(source.Canceled.Task.IsCompleted);
        source.Release(bytes[262144..]);
        source.Complete();
        var converted = await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("aac", AudioMediaFacts.ReadPrefix(converted, converted.Length)!.Media.Codec);
        Assert.Equal(1, requests);
        using var available = await gate.EnterAsync(default);
    }
}
