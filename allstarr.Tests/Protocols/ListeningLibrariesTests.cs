using allstarr.Core.Protocols;

namespace allstarr.Tests.Protocols;

public sealed class ListeningLibrariesTests
{
    private static readonly BackendLibraryAccess Access = new(true, ["kids", "mine", "shared"]);

    [Fact]
    public void AChoiceNarrowsAccessToTheChosenLibraries() =>
        Assert.Equal(["mine", "shared"], ListeningLibraries.Narrow(Access, ["shared", "mine"]).LibraryIds);

    [Fact]
    public void NoChoiceKeepsEveryLibrary() =>
        Assert.Same(Access, ListeningLibraries.Narrow(Access, null));

    [Fact]
    public void AChoiceThatNoLongerMatchesFallsBackToEveryLibrary() =>
        Assert.Same(Access, ListeningLibraries.Narrow(Access, ["gone"]));

    [Fact]
    public void UnavailableAccessStaysUnavailable() =>
        Assert.False(ListeningLibraries.Narrow(BackendLibraryAccess.Unavailable, ["mine"]).Succeeded);
}
