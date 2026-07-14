using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class FirstPartyExtensionPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-first-party-packages", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("deezer", ProviderCapabilityKind.Metadata, false)]
    [InlineData("spotify", ProviderCapabilityKind.Playlist, true)]
    [InlineData("apple-musickit", ProviderCapabilityKind.Playlist, true)]
    public void PackageBoundary_IsValidSdkV1AndDeterministic(string providerId, ProviderCapabilityKind capability, bool accountRequired)
    {
        var source = Path.Combine(RepositoryRoot(), "first-party", "providers", providerId);
        Directory.CreateDirectory(_root);
        var first = FirstPartyExtensionPackages.Build(source, Path.Combine(_root, providerId + "-1.zip"));
        var second = FirstPartyExtensionPackages.Build(source, Path.Combine(_root, providerId + "-2.zip"));

        Assert.Equal(providerId, first.Manifest.Id);
        Assert.Equal(capability, first.Manifest.Capabilities.Single().Kind);
        Assert.Equal(accountRequired, first.Manifest.Capabilities.Single().AccountRequired);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
        var verified = ExtensionSdkV1.VerifyArchive(first.Path, first.Sha256, Path.Combine(_root, providerId + "-verified"));
        Assert.Equal(first.ContentSha256, verified.ContentSha256);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
