namespace allstarr.Models.Settings;

public sealed class SpotifyApiSettings
{
    public const string DefaultPathfinderEndpoint = "https://api-partner.spotify.com/pathfinder/v1/query";
    public string PathfinderEndpoint { get; set; } = DefaultPathfinderEndpoint;
    public const string DefaultLibraryQueryHash = "50650f72ea32a99b5b46240bee22fea83024eec302478a9a75cfd05a0814ba99";
    public string LibraryQueryHash { get; set; } = DefaultLibraryQueryHash;
    public const string DefaultPlaylistQueryHash = "19ff1327c29e99c208c86d7a9d8f1929cfdf3d3202a0ff4253c821f1901aa94d";
    public string PlaylistQueryHash { get; set; } = DefaultPlaylistQueryHash;
    public const string DefaultLikedSongsQueryHash = "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240";
    public string LikedSongsQueryHash { get; set; } = DefaultLikedSongsQueryHash;
    public const string DefaultTotpSecretsUrl = "https://raw.githubusercontent.com/xyloflake/spot-secrets-go/refs/heads/main/secrets/secretBytes.json";
    public string TotpSecretsUrl { get; set; } = DefaultTotpSecretsUrl;
    public const string DefaultTokenEndpoint = "https://open.spotify.com/api/token";
    public string TokenEndpoint { get; set; } = DefaultTokenEndpoint;

    // Source policy controls availability; accept older binders without retaining a second switch.
    public bool Enabled { get => true; set { } }
    public string SessionCookie { get; set; } = string.Empty;
    public int CacheDurationMinutes { get; set; } = 60;
    public int RateLimitDelayMs { get; set; } = 100;
    public bool PreferIsrcMatching { get; set; } = true;
    public string? SessionCookieSetDate { get; set; }
}
