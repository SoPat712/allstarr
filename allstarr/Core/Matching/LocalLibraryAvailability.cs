namespace allstarr.Core.Matching;

internal static class LocalLibraryAvailability
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    // Several missed refreshes, not one slow scan, mark the local library as unavailable for matching.
    internal static readonly TimeSpan MaximumIndexAge = TimeSpan.FromHours(1);
}
