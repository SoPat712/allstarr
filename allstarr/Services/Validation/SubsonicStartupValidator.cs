using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Validation;

/// <summary>
/// Validates Subsonic server connectivity at startup
/// </summary>
public class SubsonicStartupValidator : BaseStartupValidator
{
    private readonly IOptions<SubsonicSettings> _subsonicSettings;

    public override string ServiceName => "Subsonic";

    public SubsonicStartupValidator(IOptions<SubsonicSettings> subsonicSettings, HttpClient httpClient)
        : base(httpClient)
    {
        _subsonicSettings = subsonicSettings;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var subsonicUrl = _subsonicSettings.Value.Url;

        if (string.IsNullOrWhiteSpace(subsonicUrl))
        {
            return ValidationResult.NotConfigured("Subsonic URL not configured");
        }

        try
        {
            var pingUrl = $"{subsonicUrl.TrimEnd('/')}/rest/ping.view?v=1.16.1&c=allstarr&f=json";
            var response = await _httpClient.GetAsync(pingUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                return content.Contains("\"status\":\"ok\"") || content.Contains("status=\"ok\"")
                    ? ValidationResult.Success("Subsonic server is accessible")
                    : ValidationResult.Success("Subsonic server is reachable");
            }
            else
            {
                return ValidationResult.Failure($"HTTP {(int)response.StatusCode}",
                    "Subsonic server returned an error", ConsoleColor.Red);
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
                "The Subsonic server could not be reached",
                ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                "ERROR",
                $"Subsonic validation failed ({ex.GetType().Name})",
                ConsoleColor.Red);
        }
    }
}
