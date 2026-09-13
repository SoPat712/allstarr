using allstarr.Models.Download;

namespace allstarr.Services;

public interface IDownloadService
{
    Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a song and streams the result progressively.
    /// When qualityOverride is specified (not null and not Original), downloads at the requested
    /// quality tier instead of the configured .env quality. Used for client-requested "transcoding".
    /// The .env quality acts as a ceiling — client requests can only go equal or lower.
    /// </summary>
    Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, Common.StreamQuality? qualityOverride = null, CancellationToken cancellationToken = default);

    DownloadInfo? GetDownloadStatus(string songId);

    IReadOnlyList<DownloadInfo> GetActiveDownloads();

    Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId);

    Task<bool> IsAvailableAsync();
}

public interface IConcreteDownloadService : IDownloadService
{
    string ProviderId { get; }
}
