namespace allstarr.Core.Capabilities;

public static class AudioQualityPolicy
{
    public const string SettingKey = "Audio:Quality";
    public const string DefaultStep = "BestAvailable";

    public static IReadOnlyList<string> Steps { get; } =
        ["DataSaver", "High", "CdLossless", "HiResLossless", DefaultStep];

    public static (string Apple, string Deezer, string Qobuz) ProviderCeilings(string step) => step switch
    {
        "DataSaver" => ("aac-96", "MP3_128", "MP3_320"),
        "High" => ("aac-320", "MP3_320", "MP3_320"),
        "CdLossless" => ("alac-16-44", "FLAC", "FLAC_16"),
        "HiResLossless" => ("alac-24-96", "FLAC", "FLAC_24_LOW"),
        DefaultStep => ("alac-24-192", "FLAC", "FLAC_24_HIGH"),
        _ => throw new ArgumentException($"Audio quality step '{step}' is not supported.", nameof(step))
    };

    public static string FromProviderCeilings(string? apple, string? deezer, string? qobuz)
    {
        var rank = Math.Min(AppleRank(apple), Math.Min(DeezerRank(deezer), QobuzRank(qobuz)));
        return Steps[rank];
    }

    private static int AppleRank(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "aac-96" => 0,
        "aac-320" => 1,
        "alac-16-44" => 2,
        "alac-24-48" or "alac-24-96" => 3,
        "alac-24-192" => 4,
        _ => 2
    };

    private static int DeezerRank(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MP3_128" or "128" => 0,
        "MP3_320" or "320" => 1,
        _ => 4
    };

    private static int QobuzRank(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MP3_320" or "MP3" => 1,
        "FLAC_16" or "CD" => 2,
        "FLAC_24_LOW" or "24_96" => 3,
        _ => 4
    };
}
