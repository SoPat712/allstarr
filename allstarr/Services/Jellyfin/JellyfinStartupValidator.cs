using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Validation;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Validates Jellyfin server connectivity at startup.
/// </summary>
public class JellyfinStartupValidator : BaseStartupValidator
{
    private readonly IOptions<JellyfinSettings> _settings;

    public override string ServiceName => "Jellyfin";

    public JellyfinStartupValidator(IOptions<JellyfinSettings> settings, HttpClient httpClient)
        : base(httpClient)
    {
        _settings = settings;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Value;

        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            WriteStatus("Jellyfin URL", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Jellyfin__Url environment variable");
            return ValidationResult.NotConfigured("Jellyfin URL not configured");
        }

        WriteStatus("Jellyfin URL", settings.Url, ConsoleColor.Cyan);

        // API Key is optional - only needed for server-to-server operations
        // Client authentication uses username/password via /Users/AuthenticateByName
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            WriteStatus("API Key", MaskSecret(settings.ApiKey), ConsoleColor.DarkGray);
            WriteDetail("(Optional - for server operations)");
        }

        if (!string.IsNullOrWhiteSpace(settings.UserId))
        {
            WriteStatus("User ID", MaskSecret(settings.UserId), ConsoleColor.DarkGray);
            WriteDetail("(Optional - for server operations)");
        }

        try
        {
            // Test connection using public system info endpoint (no auth required)
            var publicInfoUrl = $"{settings.Url.TrimEnd('/')}/System/Info/Public";
            var response = await _httpClient.GetAsync(publicInfoUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                // Try to parse server info
                string? serverName = null;
                string? version = null;

                if (content.Contains("ServerName"))
                {
                    var nameStart = content.IndexOf("\"ServerName\":", StringComparison.Ordinal);
                    if (nameStart >= 0)
                    {
                        nameStart = content.IndexOf('"', nameStart + 13) + 1;
                        var nameEnd = content.IndexOf('"', nameStart);
                        if (nameEnd > nameStart)
                        {
                            serverName = content[nameStart..nameEnd];
                        }
                    }
                }

                if (content.Contains("Version"))
                {
                    var verStart = content.IndexOf("\"Version\":", StringComparison.Ordinal);
                    if (verStart >= 0)
                    {
                        verStart = content.IndexOf('"', verStart + 10) + 1;
                        var verEnd = content.IndexOf('"', verStart);
                        if (verEnd > verStart)
                        {
                            version = content[verStart..verEnd];
                        }
                    }
                }

                var serverInfo = !string.IsNullOrEmpty(serverName) 
                    ? $"{serverName} (v{version ?? "unknown"})"
                    : "OK";

                WriteStatus("Jellyfin server", serverInfo, ConsoleColor.Green);

                // Test authenticated access if API key is configured
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    await ValidateAuthenticatedAccessAsync(settings, cancellationToken);
                }

                return ValidationResult.Success($"Connected to {serverInfo}");
            }
            else
            {
                WriteStatus("Jellyfin server", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return ValidationResult.Failure($"HTTP {(int)response.StatusCode}",
                    "Jellyfin server returned an error", ConsoleColor.Red);
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("Jellyfin server", "TIMEOUT", ConsoleColor.Red);
            WriteDetail("Could not reach server within 10 seconds");
            return ValidationResult.Failure("TIMEOUT", "Could not reach server within timeout period", ConsoleColor.Red);
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("Jellyfin server", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNREACHABLE", ex.Message, ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            WriteStatus("Jellyfin server", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("ERROR", ex.Message, ConsoleColor.Red);
        }
    }

    private async Task ValidateAuthenticatedAccessAsync(JellyfinSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var authHeader = $"MediaBrowser Client=\"{settings.ClientName}\", " +
                           $"Device=\"{settings.DeviceName}\", " +
                           $"DeviceId=\"{settings.DeviceId}\", " +
                           $"Version=\"{settings.ClientVersion}\", " +
                           $"Token=\"{settings.ApiKey}\"";

            using var request = new HttpRequestMessage(HttpMethod.Get, 
                $"{settings.Url.TrimEnd('/')}/System/Info");
            request.Headers.Add("Authorization", authHeader);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("Authentication", "OK", ConsoleColor.Green);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                WriteStatus("Authentication", "INVALID API KEY", ConsoleColor.Red);
                WriteDetail("Check your Jellyfin API key configuration");
            }
            else
            {
                WriteStatus("Authentication", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
            }

            // Check if we can access the music library
            if (!string.IsNullOrWhiteSpace(settings.LibraryId))
            {
                WriteStatus("Library ID", settings.LibraryId, ConsoleColor.DarkGray);
            }
            else
            {
                await TryDetectMusicLibraryAsync(settings, authHeader, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            WriteStatus("Authentication", "ERROR", ConsoleColor.Yellow);
            WriteDetail(ex.Message);
        }
    }

    private async Task TryDetectMusicLibraryAsync(JellyfinSettings settings, string authHeader, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{settings.Url.TrimEnd('/')}/Library/MediaFolders";
            if (!string.IsNullOrWhiteSpace(settings.UserId))
            {
                url += $"?userId={settings.UserId}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", authHeader);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (content.Contains("\"CollectionType\":\"music\""))
                {
                    WriteStatus("Music library", "DETECTED", ConsoleColor.Green);
                }
                else
                {
                    WriteStatus("Music library", "NOT FOUND", ConsoleColor.Yellow);
                    WriteDetail("No music library detected. Set Jellyfin__LibraryId to specify one.");
                }
            }
        }
        catch
        {
            // Silently ignore - not critical for startup
        }
    }
}
