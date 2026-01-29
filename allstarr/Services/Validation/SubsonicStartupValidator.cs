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
            WriteStatus("Subsonic URL", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Subsonic__Url environment variable");
            return ValidationResult.NotConfigured("Subsonic URL not configured");
        }

        WriteStatus("Subsonic URL", subsonicUrl, ConsoleColor.Cyan);

        try
        {
            var pingUrl = $"{subsonicUrl.TrimEnd('/')}/rest/ping.view?v=1.16.1&c=allstarr&f=json";
            var response = await _httpClient.GetAsync(pingUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (content.Contains("\"status\":\"ok\"") || content.Contains("status=\"ok\""))
                {
                    WriteStatus("Subsonic server", "OK", ConsoleColor.Green);
                    return ValidationResult.Success("Subsonic server is accessible");
                }
                else if (content.Contains("\"status\":\"failed\"") || content.Contains("status=\"failed\""))
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Authentication may be required for some operations");
                    return ValidationResult.Success("Subsonic server is reachable");
                }
                else
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Unexpected response format");
                    return ValidationResult.Success("Subsonic server is reachable");
                }
            }
            else
            {
                WriteStatus("Subsonic server", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return ValidationResult.Failure($"HTTP {(int)response.StatusCode}", 
                    "Subsonic server returned an error", ConsoleColor.Red);
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("Subsonic server", "TIMEOUT", ConsoleColor.Red);
            WriteDetail("Could not reach server within 10 seconds");
            return ValidationResult.Failure("TIMEOUT", "Could not reach server within timeout period", ConsoleColor.Red);
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("Subsonic server", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNREACHABLE", ex.Message, ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            WriteStatus("Subsonic server", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("ERROR", ex.Message, ConsoleColor.Red);
        }
    }
}
