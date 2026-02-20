namespace allstarr.Services.Common;

/// <summary>
/// Service that runs on startup to migrate old .env file format to new format
/// </summary>
public class EnvMigrationService
{
    private readonly ILogger<EnvMigrationService> _logger;
    private readonly string _envFilePath;

    public EnvMigrationService(ILogger<EnvMigrationService> logger)
    {
        _logger = logger;
        _envFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    }

    public void MigrateEnvFile()
    {
        if (!File.Exists(_envFilePath))
        {
            _logger.LogWarning("No .env file found, skipping migration");
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_envFilePath);
            var modified = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                // Skip comments and empty lines
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                // Migrate DOWNLOAD_PATH to Library__DownloadPath
                if (line.StartsWith("DOWNLOAD_PATH="))
                {
                    var value = line.Substring("DOWNLOAD_PATH=".Length);
                    lines[i] = $"Library__DownloadPath={value}";
                    modified = true;
                    _logger.LogDebug("Migrated DOWNLOAD_PATH to Library__DownloadPath in .env file");
                }
                
                // Migrate old SquidWTF quality values to new format
                if (line.StartsWith("SQUIDWTF_QUALITY="))
                {
                    var value = line.Substring("SQUIDWTF_QUALITY=".Length).Trim();
                    var newValue = value.ToUpperInvariant() switch
                    {
                        "FLAC" => "LOSSLESS",
                        "HI_RES" => "HI_RES_LOSSLESS",
                        "MP3_320" => "HIGH",
                        "MP3_128" => "LOW",
                        _ => null // Keep as-is if already correct
                    };
                    
                    if (newValue != null)
                    {
                        lines[i] = $"SQUIDWTF_QUALITY={newValue}";
                        modified = true;
                        _logger.LogInformation("Migrated SQUIDWTF_QUALITY from {Old} to {New} in .env file", value, newValue);
                    }
                }
                
                // CRITICAL FIX: Remove quotes from password/token values
                // Docker Compose does NOT need quotes in .env files - it handles special characters correctly
                // When quotes are used, they become part of the value itself
                var keysToUnquote = new[]
                {
                    "SCROBBLING_LASTFM_PASSWORD",
                    "MUSICBRAINZ_PASSWORD",
                    "DEEZER_ARL",
                    "DEEZER_ARL_FALLBACK",
                    "QOBUZ_USER_AUTH_TOKEN",
                    "SCROBBLING_LASTFM_SESSION_KEY",
                    "SCROBBLING_LISTENBRAINZ_USER_TOKEN",
                    "SPOTIFY_API_SESSION_COOKIE"
                };
                
                foreach (var key in keysToUnquote)
                {
                    if (line.StartsWith($"{key}="))
                    {
                        var value = line.Substring($"{key}=".Length);
                        
                        // Remove surrounding quotes if present
                        if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                        {
                            var unquoted = value.Substring(1, value.Length - 2);
                            lines[i] = $"{key}={unquoted}";
                            modified = true;
                            _logger.LogInformation("Removed quotes from {Key} (Docker Compose doesn't need them)", key);
                        }
                        break;
                    }
                }
            }

            if (modified)
            {
                File.WriteAllLines(_envFilePath, lines);
                _logger.LogInformation("✅ .env file migration completed successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate .env file");
        }
    }
}
