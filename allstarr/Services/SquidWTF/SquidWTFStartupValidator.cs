using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Validation;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup (no auth needed)
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private readonly SquidWTFSettings _settings;
	private readonly List<string> _apiUrls;
    private int _currentUrlIndex = 0;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(IOptions<SquidWTFSettings> settings, HttpClient httpClient, List<string> apiUrls)
        : base(httpClient)
    {
        _settings = settings.Value;
        _apiUrls = apiUrls;
    }
    
    private async Task<T> TryWithFallbackAsync<T>(Func<string, Task<T>> action, T defaultValue)
    {
        for (int attempt = 0; attempt < _apiUrls.Count; attempt++)
        {
            try
            {
                var baseUrl = _apiUrls[_currentUrlIndex];
                return await action(baseUrl);
            }
            catch
            {
                WriteDetail($"Endpoint {_apiUrls[_currentUrlIndex]} failed, trying next...");
                _currentUrlIndex = (_currentUrlIndex + 1) % _apiUrls.Count;
                
                if (attempt == _apiUrls.Count - 1)
                {
                    return defaultValue;
                }
            }
        }
        return defaultValue;
    }	
	
    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();

        var quality = _settings.Quality?.ToUpperInvariant() switch
        {
            "FLAC" => "LOSSLESS",
            "HI_RES" => "HI_RES_LOSSLESS",
            "LOSSLESS" => "LOSSLESS",
            "HIGH" => "HIGH",
            "LOW" => "LOW",
            _ => "LOSSLESS (default)"
        };

        WriteStatus("SquidWTF Quality", quality, ConsoleColor.Cyan);

        // Test connectivity with fallback
        var result = await TryWithFallbackAsync(async (baseUrl) =>
        {
            var response = await _httpClient.GetAsync(baseUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF API", $"REACHABLE ({baseUrl})", ConsoleColor.Green);
                WriteDetail("No authentication required - powered by Tidal");
                
                // Try a test search to verify functionality
                await ValidateSearchFunctionality(baseUrl, cancellationToken);
                
                return ValidationResult.Success("SquidWTF validation completed");
            }
            else
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }
        }, ValidationResult.Failure("-1", "All SquidWTF endpoints failed"));

        return result;
    }

    private async Task ValidateSearchFunctionality(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            // Test search with a simple query
            var searchUrl = $"{baseUrl}/search/?s=Taylor%20Swift";
            var searchResponse = await _httpClient.GetAsync(searchUrl, cancellationToken);

            if (searchResponse.IsSuccessStatusCode)
            {
                var json = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("items", out var items))
                {
                    var itemCount = items.GetArrayLength();
                    WriteStatus("Search Functionality", "WORKING", ConsoleColor.Green);
                    WriteDetail($"Test search returned {itemCount} results");
                }
                else
                {
                    WriteStatus("Search Functionality", "UNEXPECTED RESPONSE", ConsoleColor.Yellow);
                }
            }
            else
            {
                WriteStatus("Search Functionality", $"HTTP {(int)searchResponse.StatusCode}", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex)
        {
            WriteStatus("Search Functionality", "ERROR", ConsoleColor.Yellow);
            WriteDetail($"Could not verify search: {ex.Message}");
        }
    }
}