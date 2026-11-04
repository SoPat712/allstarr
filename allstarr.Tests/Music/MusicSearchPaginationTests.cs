using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Moq;

namespace allstarr.Tests;

public sealed class MusicSearchPaginationTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public void LaterPagesAppendLateAnswersAfterTheReturnedPrefixWithoutDuplicates(ProtocolKind protocol)
    {
        var service = new MusicSearchService(Mock.Of<IProtocolProviderGateway>());
        var context = Context(protocol);
        Assert.Equal(["first"], Page(["first", "second"], 0));
        Assert.Equal(["late"], Page(["late", "first", "second", "late"], 1));
        Assert.Equal(["second"], Page(["second", "late", "first"], 2));
        Assert.Equal(["first", "late", "second"],
            service.PreserveSearchOrder(context, "Track", "songs", 1, 2,
                new[] { "second", "late", "first" }, item => item));
        service.PreserveSearchOrder(context, "Track", "songs", 0, 0, new[] { "unrequested" }, item => item);
        Assert.Equal(["first", "late", "second"], service.PreserveSearchOrder(context, "Track", "songs", 1, 1,
            new[] { "second", "late", "first" }, item => item));

        IEnumerable<string> Page(string[] values, int offset) => service
            .PreserveSearchOrder(context, "Track", "songs", offset, 1, values, item => item)
            .Skip(offset).Take(1);
    }

    [Fact]
    public void ANewFirstPageRefreshesTheSequenceAndScopesStaySeparate()
    {
        var service = new MusicSearchService(Mock.Of<IProtocolProviderGateway>());
        var context = Context(ProtocolKind.Jellyfin);
        service.PreserveSearchOrder(context, "Track", "Audio", 0, 1, new[] { "old" }, item => item);
        Assert.Equal(["new", "old"], service.PreserveSearchOrder(context, "Track", "Audio", 0, 1,
            new[] { "new", "old" }, item => item));
        Assert.Equal(["other"], service.PreserveSearchOrder(context, "Track", "MusicAlbum", 1, 1,
            new[] { "other" }, item => item));
        var other = new ProtocolExecutionContext(context.Protocol, "backend", "other", null, "other",
            context.Deadline, default);
        Assert.Equal(["other"], service.PreserveSearchOrder(other, "Track", "Audio", 1, 1,
            new[] { "other" }, item => item));
    }

    [Fact]
    public void ContinuingPagesUseFreshPayloadsForPreviouslyReturnedIds()
    {
        var service = new MusicSearchService(Mock.Of<IProtocolProviderGateway>());
        var context = Context(ProtocolKind.Jellyfin);
        service.PreserveSearchOrder(context, "Track", "songs", 0, 1,
            new[] { (Id: "first", Payload: "old") }, item => item.Id);
        var continued = service.PreserveSearchOrder(context, "Track", "songs", 1, 1,
            new[] { (Id: "late", Payload: "late"), (Id: "first", Payload: "fresh") }, item => item.Id);
        Assert.Equal(["first", "late"], continued.Select(item => item.Id));
        Assert.Equal("fresh", continued[0].Payload);
    }

    [Fact]
    public void SubsonicKeepsMergedFirstPageOverfillAndMapsRepeatedAndOverlappingWindows()
    {
        var service = new MusicSearchService(Mock.Of<IProtocolProviderGateway>());
        var context = Context(ProtocolKind.Subsonic);
        Assert.Equal(["native-first", "native-second", "provider"], Page(0, 2,
            ["native-first", "native-second", "provider"]));
        Assert.Empty(Page(0, 0, ["unrequested"]));
        Assert.Equal(["late", "native-third"], Page(2, 2,
            ["late", "native-third", "provider", "late"]));
        Assert.Equal(["late", "native-third"], Page(2, 2,
            ["native-third", "late", "provider"]));
        Assert.Equal(["native-second", "provider"], Page(1, 2, ["late", "native-third", "provider"]));
        Assert.Equal(["next"], Page(4, 2, ["next", "late", "native-third", "provider"]));

        IReadOnlyList<string> Page(int offset, int count, string[] items) => service.ApplySubsonicSearchWindow(
            context, "Track", "songs", offset, count, items, item => item);
    }

    [Fact]
    public void PaginationPrefixesExpireAndTheNumberOfSearchesIsBounded()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), timeProvider: clock);
        var context = Context(ProtocolKind.Jellyfin);
        service.PreserveSearchOrder(context, "expired", "songs", 0, 1, new[] { "old" }, item => item);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(["new"], service.PreserveSearchOrder(context, "expired", "songs", 1, 1,
            new[] { "new" }, item => item));
        for (var index = 0; index < 257; index++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            service.PreserveSearchOrder(context, "Track " + index, "songs", 0, 1, new[] { "old" }, item => item);
        }
        Assert.Equal(["new"], service.PreserveSearchOrder(context, "Track 0", "songs", 1, 1,
            new[] { "new" }, item => item));
    }

    private static ProtocolExecutionContext Context(ProtocolKind protocol) => new(
        protocol, "backend", "listener", null, "pagination", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
