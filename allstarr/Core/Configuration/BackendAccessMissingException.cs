namespace allstarr.Core.Configuration;

public sealed class BackendAccessMissingException() : InvalidOperationException(MessageText)
{
    public const string Code = "backend_access_missing";
    public const string MessageText = "Media-server access is missing or unreadable. Restore it in Settings → Access → Media-server access to resume playlist sync and library scans.";
}
