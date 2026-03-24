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

                // Migrate Library__DownloadPath to DOWNLOAD_PATH (inverse migration)
                if (line.StartsWith("Library__DownloadPath="))
                {
                    var value = line.Substring("Library__DownloadPath=".Length);
                    lines[i] = $"DOWNLOAD_PATH={value}";
                    modified = true;
                    _logger.LogInformation("Migrated Library__DownloadPath to DOWNLOAD_PATH in .env file");
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

            ReformatEnvFileIfSquashed();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate .env file");
        }
    }

    private void ReformatEnvFileIfSquashed()
    {
        try
        {
            if (!File.Exists(_envFilePath)) return;

            var currentLines = File.ReadAllLines(_envFilePath);
            var commentCount = currentLines.Count(l => l.TrimStart().StartsWith("#"));
            
            // If the file has fewer than 5 comments, it's likely a flattened/squashed file
            // from an older version or raw docker output. Let's rehydrate it.
            if (commentCount < 5)
            {
                var examplePath = Path.Combine(Directory.GetCurrentDirectory(), ".env.example");
                if (!File.Exists(examplePath))
                {
                    examplePath = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", ".env.example");
                }

                if (!File.Exists(examplePath)) return;

                _logger.LogInformation("Flattened/raw .env file detected (only {Count} comments). Rehydrating formatting from .env.example...", commentCount);

                var currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in currentLines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = trimmed[..eqIndex].Trim();
                        var value = trimmed[(eqIndex + 1)..].Trim();
                        currentValues[key] = value;
                    }
                }

                var exampleLines = File.ReadAllLines(examplePath).ToList();
                var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < exampleLines.Count; i++)
                {
                    var line = exampleLines[i].TrimStart();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (!line.StartsWith("#"))
                    {
                        var eqIndex = line.IndexOf('=');
                        if (eqIndex > 0)
                        {
                            var key = line[..eqIndex].Trim();
                            if (currentValues.TryGetValue(key, out var val))
                            {
                                exampleLines[i] = $"{key}={val}";
                                usedKeys.Add(key);
                            }
                        }
                    }
                    else
                    {
                        var eqIndex = line.IndexOf('=');
                        if (eqIndex > 0)
                        {
                            var keyPart = line[..eqIndex].TrimStart('#').Trim();
                            if (!keyPart.Contains(" ") && keyPart.Length > 0 && currentValues.TryGetValue(keyPart, out var val))
                            {
                                exampleLines[i] = $"{keyPart}={val}";
                                usedKeys.Add(keyPart);
                            }
                        }
                    }
                }

                var leftoverKeys = currentValues.Keys.Except(usedKeys).ToList();
                if (leftoverKeys.Any())
                {
                    exampleLines.Add("");
                    exampleLines.Add("# ===== CUSTOM / UNKNOWN VARIABLES =====");
                    foreach (var key in leftoverKeys)
                    {
                        exampleLines.Add($"{key}={currentValues[key]}");
                    }
                }

                File.WriteAllLines(_envFilePath, exampleLines);
                _logger.LogInformation("✅ .env file successfully rehydrated with comments and formatting");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rehydrate .env file formatting");
        }
    }
}
