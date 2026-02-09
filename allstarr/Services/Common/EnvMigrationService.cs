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
            _logger.LogDebug("No .env file found, skipping migration");
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
                    _logger.LogInformation("Migrated DOWNLOAD_PATH to Library__DownloadPath in .env file");
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
            _logger.LogWarning(ex, "Failed to migrate .env file - please manually update DOWNLOAD_PATH to Library__DownloadPath");
        }
    }
}
