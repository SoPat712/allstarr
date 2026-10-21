using System.Text;
using System.Diagnostics;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class AudioQualityAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"audio-quality-{Guid.NewGuid():N}");
    private readonly FileMediaApplicationCache _cache;
    private readonly AudioQualityAnalyzer _analyzer;

    public AudioQualityAnalyzerTests()
    {
        Directory.CreateDirectory(_root);
        _cache = new(new FileMediaCacheOptions(Path.Combine(_root, "cache")), new SystemPlatformClock(),
            NullLogger<FileMediaApplicationCache>.Instance);
        _analyzer = new(_cache);
    }

    [Fact]
    public async Task BandLimitedHighRateSweepSuggestsUpsampling()
    {
        var path = Wave("sweep.wav", 96000, 24, Sweep(96000, 20000));
        var result = await _analyzer.AnalyzeAsync(path);
        Assert.Equal("upsampled", result.Verdict);
        Assert.InRange(result.EffectiveBandwidthHz!.Value, 19000, 22500);
        Assert.Equal(44100, result.LikelySourceRate);
        Assert.InRange(result.Confidence, 0, 1);
    }

    [Fact]
    public async Task CutoffAboveCompactDiscNyquistSuggestsFortyEightKilohertzSource()
    {
        var result = await _analyzer.AnalyzeAsync(Wave("forty-eight.wav", 96000, 24, Sweep(96000, 23500)));
        Assert.Equal("upsampled", result.Verdict);
        Assert.Equal(48000, result.LikelySourceRate);
    }

    [Fact]
    public async Task FullBandHighRateNoiseSupportsHighResolution()
    {
        var result = await _analyzer.AnalyzeAsync(Wave("wide.wav", 96000, 24, Noise(96000)));
        Assert.Equal("true-hi-res", result.Verdict);
        Assert.Equal(96000, result.ClaimedSampleRate);
        Assert.Equal(24, result.ClaimedBitDepth);
        Assert.True(result.EffectiveBandwidthHz > 40000);
    }

    [Fact]
    public async Task ZeroLowByteRevealsPaddedDepth()
    {
        var path = Wave("padded.wav", 44100, 24, Noise(44100), padded: true);
        Assert.Equal("padded-bit-depth", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Fact]
    public async Task RepeatedLowByteAlsoSuggestsPaddedDepth()
    {
        var path = Wave("offset-padded.wav", 44100, 24, Noise(44100), padded: true);
        var data = await File.ReadAllBytesAsync(path);
        for (var offset = 44; offset < data.Length; offset += 3) data[offset] = 1;
        await File.WriteAllBytesAsync(path, data);
        Assert.Equal("padded-bit-depth", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Fact]
    public async Task LowBytesAreInspectedAcrossChannels()
    {
        var path = Wave("stereo.wav", 96000, 24, Noise(96000), stereo: true);
        Assert.Equal("true-hi-res", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Fact]
    public async Task OneUnpaddedChannelPreventsPaddedDepthVerdict()
    {
        var path = Wave("mixed.wav", 96000, 24, Noise(96000), stereo: true, firstChannelPadded: true);
        Assert.Equal("true-hi-res", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Theory]
    [InlineData(96000, 20000, "upsampled")]
    [InlineData(44100, 16000, "lossy-transcode-suspected")]
    public async Task LosslessCompressedInputUsesTheSameSpectrumChecksWhenDecoderIsInstalled(int rate, int cutoff, string expected)
    {
        var source = Wave("source.wav", rate, 24, Sweep(rate, cutoff));
        var destination = Path.Combine(_root, "source.flac");
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var value in new[] { "-v", "error", "-nostdin", "-i", source, "-c:a", "flac", destination })
            info.ArgumentList.Add(value);
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { return; }
        Assert.NotNull(process);
        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            using var registration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
        }
        Assert.Equal(expected, (await _analyzer.AnalyzeAsync(destination)).Verdict);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(18500)]
    public async Task SharpCutoffInLosslessAudioSuggestsLossyHistory(int cutoff)
    {
        var path = Wave("filtered.wav", 44100, 16, Sweep(44100, cutoff));
        Assert.Equal("lossy-transcode-suspected", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Fact]
    public async Task OrdinaryCompactDiscAudioIsStandard()
    {
        var path = Wave("cd.wav", 44100, 16, Noise(44100));
        Assert.Equal("standard", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilenceAndNarrowTonesAreInconclusive(bool tone)
    {
        var samples = new double[96000];
        if (tone)
            for (var i = 0; i < samples.Length; i++) samples[i] = 0.4 * Math.Sin(2 * Math.PI * 1000 * i / 96000);
        var result = await _analyzer.AnalyzeAsync(Wave("quiet.wav", 96000, 24, samples));
        Assert.Equal("unknown", result.Verdict);
    }

    [Fact]
    public async Task CacheIsReusedForcedAndInvalidatedByFileIdentity()
    {
        var path = Wave("identity.wav", 44100, 16, Noise(44100));
        await _analyzer.AnalyzeAsync(path);
        var first = await _cache.GetUsageAsync();
        await _analyzer.AnalyzeAsync(path);
        var second = await _cache.GetUsageAsync();
        Assert.True(second.Hits > first.Hits);
        await _analyzer.AnalyzeAsync(path, force: true);
        var forced = await _cache.GetUsageAsync();
        Assert.True(forced.Writes > second.Writes);
        Wave("identity.wav", 44100, 16, new double[44100]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("unknown", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToAnUnknownResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _analyzer.AnalyzeAsync(Wave("cancel.wav", 44100, 16, Noise(44100)), cancellation.Token));
    }

    [Fact]
    public async Task InvalidWaveReturnsUnknown()
    {
        var path = Path.Combine(_root, "invalid.wav");
        await File.WriteAllTextAsync(path, "RIFF");
        Assert.Equal("unknown", (await _analyzer.AnalyzeAsync(path)).Verdict);
    }

    private string Wave(string name, int rate, int bits, double[] samples, bool padded = false, bool stereo = false,
        bool firstChannelPadded = false)
    {
        var path = Path.Combine(_root, name);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        var channels = stereo ? 2 : 1;
        var bytes = bits / 8;
        var length = samples.Length * channels * bytes;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(length + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
        writer.Write(rate * channels * bytes); writer.Write((short)(channels * bytes)); writer.Write((short)bits);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length);
        foreach (var sample in samples)
        {
            var value = (int)Math.Round(Math.Clamp(sample, -0.99, 0.99) * ((1L << (bits - 1)) - 1));
            if (padded) value = value >> 8 << 8;
            for (var channel = 0; channel < channels; channel++)
            {
                var channelValue = channel == 0 ? value : -value;
                if (firstChannelPadded && channel == 0) channelValue = channelValue >> 8 << 8;
                for (var index = 0; index < bytes; index++) writer.Write((byte)(channelValue >> (index * 8)));
            }
        }
        return path;
    }

    private static double[] Noise(int rate)
    {
        var random = new Random(4287);
        return Enumerable.Range(0, rate * 2).Select(_ => (random.NextDouble() - 0.5) * 0.8).ToArray();
    }

    private static double[] Sweep(int rate, int cutoff)
    {
        var samples = new double[rate * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var time = (double)i / rate;
            var phase = 2 * Math.PI * (200 * time + (cutoff - 200) * time * time / 6);
            samples[i] = 0.7 * Math.Sin(phase);
        }
        return samples;
    }

    public void Dispose()
    {
        _cache.Dispose();
        Directory.Delete(_root, true);
    }
}
