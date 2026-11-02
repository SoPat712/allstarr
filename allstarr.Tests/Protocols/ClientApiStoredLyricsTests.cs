using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class ClientApiStoredLyricsTests
{
    [Theory]
    [InlineData(null, "word", "lrcred")]
    [InlineData(null, "line", "kept")]
    [InlineData("kept", "word", "kept")]
    [InlineData("lrcred", "word", "lrcred")]
    public async Task StoredAvailabilityUsesQualityAndPreservesChosenSources(string? pin, string serviceFormat, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "stored-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(audio, "fixture");
            await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[00:01.000]Kept words");
            var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
            cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.Is<string>(key => key.StartsWith("lyrics:v3:lrcred:"))))
                .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrcred", ProviderLyricsFormat.LineTimed,
                    serviceFormat == "word" ? "[00:01.000]<00:01.000>Service <00:01.500>words" : "[00:01.000]Service words"));
            var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
                .Add(ProviderCapabilityKind.Lyrics, ["kept", "lrcred"]), ImmutableHashSet<string>.Empty, "lossless", .07);
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener", null,
                "stored-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var result = await StoredItemLyrics.ReadAsync(cache.Object, new Song { Title = "Fixture", Artist = "Artist" }, null,
                pin, audio, policy, "http://unused.invalid", context, [], [], default);
            Assert.True(result.Available);
            Assert.Equal(expected, result.Source);
            Assert.Equal(expected == "kept" ? "line" : serviceFormat, result.Format);
            if (pin == "kept") cache.VerifyNoOtherCalls();
            else cache.VerifyAll();
        }
        finally { Directory.Delete(root, true); }
    }
}
