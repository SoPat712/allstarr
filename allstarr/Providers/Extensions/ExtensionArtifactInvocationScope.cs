using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;

namespace allstarr.Core.Extensions;

/// <summary>
/// Binds the artifact bridge to one authorized download invocation. Extension code
/// never selects a filesystem path or another workspace.
/// </summary>
public sealed class ExtensionArtifactInvocationScope : IDisposable
{
    private static readonly AsyncLocal<ExtensionArtifactInvocationScope?> Ambient = new();
    private readonly ExtensionArtifactInvocationScope? prior;
    private readonly ProviderDownloadArtifactResolver resolver;
    private readonly ProviderManagedWorkspaceReference workspace;
    private readonly Guid durableJobId;
    private readonly string providerId;
    private readonly long maximumBytes;
    private readonly CancellationToken cancellationToken;
    private readonly string temporaryRoot;
    private bool disposed;

    private ExtensionArtifactInvocationScope(
        ProviderDownloadArtifactResolver resolver,
        ProviderManagedWorkspaceReference workspace,
        Guid durableJobId,
        string providerId,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        prior = Ambient.Value;
        this.resolver = resolver;
        this.workspace = workspace;
        this.durableJobId = durableJobId;
        this.providerId = providerId;
        this.maximumBytes = maximumBytes;
        this.cancellationToken = cancellationToken;
        temporaryRoot = Path.Combine(Path.GetTempPath(), "allstarr-extension-invocations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        Ambient.Value = this;
    }

    public static ExtensionArtifactInvocationScope? Current => Ambient.Value;
    public ProviderDownloadArtifactWriteResult? Result { get; private set; }
    public CancellationToken CancellationToken => cancellationToken;
    public long MaximumBytes => maximumBytes;

    public string ResolveTemporaryPath(string relativePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("The extension temporary path is invalid.");
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidOperationException("The extension temporary path is invalid.");
        var path = Path.GetFullPath(Path.Combine(temporaryRoot, normalized));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The extension temporary path escapes its invocation.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static ExtensionArtifactInvocationScope Open(
        ProviderDownloadArtifactResolver resolver,
        ProviderManagedWorkspaceReference workspace,
        Guid durableJobId,
        string providerId,
        long maximumBytes,
        CancellationToken cancellationToken) => new(
        resolver, workspace, durableJobId, providerId, maximumBytes, cancellationToken);

    public ProviderDownloadArtifactWriteResult Write(
        string artifactId,
        Stream content,
        long? expectedBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Result != null)
            throw new InvalidOperationException("An extension download invocation may create only one artifact.");
        Result = resolver.WriteAsync(new(
            workspace,
            durableJobId,
            providerId,
            artifactId,
            content,
            maximumBytes)
        {
            ExpectedBytes = expectedBytes is > 0 ? expectedBytes : null
        }, cancellationToken).GetAwaiter().GetResult();
        return Result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ambient.Value = prior;
        try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
        catch { }
    }
}
