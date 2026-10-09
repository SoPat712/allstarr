using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed class SubsonicLyricsLookupTests
{
    [Fact]
    public async Task MissingTypedMetadataDoesNotProbeAnotherProviderPath()
    {
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "principal", null,
            "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(context, "deezer", "missing")).ReturnsAsync((Song?)null);
        var lyrics = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        var lookup = new SubsonicLyricsLookup(gateway.Object, lyrics.Object);

        Assert.Null(await lookup.FindAsync(context, "deezer", "missing", CancellationToken.None));

        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
        lyrics.VerifyNoOtherCalls();
    }
}
