using System.Text.Json;
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
            return ValidationResult.NotConfigured("Jellyfin URL not configured");
        }


        try
        {
            var publicInfoUrl = $"{settings.Url.TrimEnd('/')}/System/Info/Public";
            var response = await _httpClient.GetAsync(publicInfoUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                var serverName = root.TryGetProperty("ServerName", out var name) ? name.GetString() : null;
                var version = root.TryGetProperty("Version", out var value) ? value.GetString() : null;
                var serverInfo = !string.IsNullOrEmpty(serverName)
                    ? $"{serverName} (v{version ?? "unknown"})"
                    : "OK";

                return ValidationResult.Success($"Connected to {serverInfo}");
            }
            else
            {
                return ValidationResult.Failure($"HTTP {(int)response.StatusCode}",
                    "Jellyfin server returned an error", ConsoleColor.Red);
            }
        }
        catch (TaskCanceledException)
        {
            return ValidationResult.Failure("TIMEOUT", "Could not reach server within timeout period", ConsoleColor.Red);
        }
        catch (HttpRequestException)
        {
            return ValidationResult.Failure(
                "UNREACHABLE",
                "The Jellyfin server could not be reached",
                ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                "ERROR",
                $"Jellyfin validation failed ({ex.GetType().Name})",
                ConsoleColor.Red);
        }
    }

}
