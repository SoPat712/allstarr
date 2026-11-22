using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

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

    [Fact]
    public async Task SearchWithoutAChoiceReadsEveryLibraryAndAsksTheMediaServerNothing()
    {
        var (libraries, inner) = Libraries(null);
        Assert.Null(await libraries.SearchedAsync(Context(), null));
        Assert.Null(await libraries.SearchedAsync(Context(), "kids"));
        inner.Verify(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchReadsTheChosenLibrariesWithinTheAppsOwnPick()
    {
        var (libraries, _) = Libraries(["mine", "shared"]);
        Assert.Equal(["mine", "shared"], await libraries.SearchedAsync(Context(), null));
        // An app's pick of a chosen library stands, however it spells the id.
        Assert.Equal(["mine"], await libraries.SearchedAsync(Context(), "MINE"));
        // A library the listener left out has nothing to show.
        Assert.Empty((await libraries.SearchedAsync(Context(), "kids"))!);
        // Anything else, such as a folder, is searched as asked within the chosen libraries.
        Assert.Equal(["mine", "shared"], await libraries.SearchedAsync(Context(), "a-folder"));
    }

    [Fact]
    public async Task ChoosingEveryLibrarySearchesAsWithoutAChoice()
    {
        var (libraries, _) = Libraries(["kids", "mine", "shared"]);
        Assert.Null(await libraries.SearchedAsync(Context(), null));
        Assert.Null(await libraries.SearchedAsync(Context(), "kids"));
    }

    private static readonly Guid Owner = Guid.NewGuid();

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "primary", "listener",
        new AllstarrPrincipal(Owner, "jellyfin", "primary", "listener", "listener", false), "search", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private static (ListeningLibraries Libraries, Mock<IBackendLibraryAccessResolver> Inner) Libraries(IReadOnlyList<string>? selected)
    {
        var inner = new Mock<IBackendLibraryAccessResolver>();
        inner.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(Access);
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(item => item.GetMyLibrariesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new MyLibrariesSetting(selected, 1));
        return (new ListeningLibraries(inner.Object, settings.Object, Mock.Of<IDbContextFactory<AllstarrDbContext>>(),
            Mock.Of<IBackendLibraryPermissionSource>(), Mock.Of<IHttpClientFactory>(), new ConfigurationBuilder().Build(), TimeProvider.System), inner);
    }
}
