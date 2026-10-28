namespace allstarr.Core.Extensions;

public sealed class ExtensionExecutionOptions
{
    public const string SectionName = "Extensions";

    public int SignedDownloadRequestSeconds { get; set; } = 120;

    public int StreamResolutionSeconds { get; set; } = 15;

    public TimeSpan GetStreamResolutionTimeout()
    {
        if (StreamResolutionSeconds is < 1 or > 30)
            throw new InvalidOperationException("Extensions:StreamResolutionSeconds must be between 1 and 30.");
        return TimeSpan.FromSeconds(StreamResolutionSeconds);
    }

    public TimeSpan GetSignedDownloadRequestTimeout()
    {
        if (SignedDownloadRequestSeconds is < 30 or > 300)
        {
            throw new InvalidOperationException(
                "Extensions:SignedDownloadRequestSeconds must be between 30 and 300.");
        }

        return TimeSpan.FromSeconds(SignedDownloadRequestSeconds);
    }
}
