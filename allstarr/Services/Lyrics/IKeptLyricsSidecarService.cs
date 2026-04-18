using allstarr.Models.Domain;

namespace allstarr.Services.Lyrics;

public interface IKeptLyricsSidecarService
{
    string GetSidecarPath(string audioFilePath);

    Task<string?> EnsureSidecarAsync(
        string audioFilePath,
        Song? song = null,
        string? externalProvider = null,
        string? externalId = null,
        CancellationToken cancellationToken = default);
}
