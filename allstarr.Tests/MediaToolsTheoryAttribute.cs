namespace allstarr.Tests;

public sealed class MediaToolsTheoryAttribute : TheoryAttribute
{
    public MediaToolsTheoryAttribute()
    {
        if (!ExtensionDownloadDecryptionTests.MediaToolsAvailable) Skip = "ffmpeg and ffprobe are unavailable on the test PATH.";
    }
}
