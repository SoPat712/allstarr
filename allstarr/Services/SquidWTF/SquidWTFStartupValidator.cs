using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Validation;
using allstarr.Services.Common;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup (no auth needed)
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private readonly SquidWTFSettings _settings;
    private readonly RoundRobinFallbackHelper _fallbackHelper;
    private readonly EndpointBenchmarkService _benchmarkService;
    private readonly ILogger<SquidWTFStartupValidator> _logger;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(
        IOptions<SquidWTFSettings> settings, 
        HttpClient httpClient, 
        List<string> apiUrls, 
        EndpointBenchmarkService benchmarkService,
        ILogger<SquidWTFStartupValidator> logger)
        : base(httpClient)
    {
        _settings = settings.Value;
        _fallbackHelper = new RoundRobinFallbackHelper(apiUrls, logger, "SquidWTF");
        _benchmarkService = benchmarkService;
        _logger = logger;
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

        // Benchmark all endpoints to determine fastest
        var apiUrls = _fallbackHelper.EndpointCount > 0 
            ? Enumerable.Range(0, _fallbackHelper.EndpointCount).Select(_ => "").ToList() // Placeholder, we'll get actual URLs from fallback helper
            : new List<string>();

        // Get the actual API URLs by reflection (not ideal, but works for now)
        var fallbackHelperType = _fallbackHelper.GetType();
        var apiUrlsField = fallbackHelperType.GetField("_apiUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (apiUrlsField != null)
        {
            apiUrls = (List<string>)apiUrlsField.GetValue(_fallbackHelper)!;
        }

        if (apiUrls.Count > 1)
        {
            WriteStatus("Benchmarking Endpoints", $"{apiUrls.Count} endpoints", ConsoleColor.Cyan);
            
            var orderedEndpoints = await _benchmarkService.BenchmarkEndpointsAsync(
                apiUrls,
                async (endpoint, ct) =>
                {
                    try
                    {
                        // 5 second timeout per ping - mark slow endpoints as failed
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                        
                        var response = await _httpClient.GetAsync(endpoint, timeoutCts.Token);
                        return response.IsSuccessStatusCode;
                    }
                    catch
                    {
                        return false;
                    }
                },
                pingCount: 5,
                cancellationToken);

            if (orderedEndpoints.Count > 0)
            {
                _fallbackHelper.SetEndpointOrder(orderedEndpoints);
                
                // Show top 5 endpoints with their metrics
                var topEndpoints = orderedEndpoints.Take(5).ToList();
                WriteDetail($"Fastest endpoint: {topEndpoints.First()}");
                
                if (topEndpoints.Count > 1)
                {
                    WriteDetail("Top 5 endpoints by average latency:");
                    for (int i = 0; i < topEndpoints.Count; i++)
                    {
                        var endpoint = topEndpoints[i];
                        var metrics = _benchmarkService.GetMetrics(endpoint);
                        if (metrics != null)
                        {
                            WriteDetail($"  {i + 1}. {endpoint} - {metrics.AverageResponseMs}ms avg ({metrics.SuccessRate:P0} success)");
                        }
                        else
                        {
                            WriteDetail($"  {i + 1}. {endpoint}");
                        }
                    }
                }
            }
        }

        // Test connectivity with fallback
        var result = await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
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
            // Test search with "22" by Taylor Swift
            var searchUrl = $"{baseUrl}/search/?s=22%20Taylor%20Swift";
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
                    WriteDetail($"Test search for '22' by Taylor Swift returned {itemCount} results");
                    
                    // Check if we found the actual song
                    bool foundTaylorSwift22 = false;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("title", out var title) &&
                            item.TryGetProperty("artists", out var artists) &&
                            artists.GetArrayLength() > 0)
                        {
                            var titleStr = title.GetString() ?? "";
                            var artistName = artists[0].TryGetProperty("name", out var name) 
                                ? name.GetString() ?? "" 
                                : "";
                            
                            if (titleStr.Contains("22", StringComparison.OrdinalIgnoreCase) &&
                                artistName.Contains("Taylor Swift", StringComparison.OrdinalIgnoreCase))
                            {
                                foundTaylorSwift22 = true;
                                var trackId = item.TryGetProperty("id", out var id) ? id.GetInt64() : 0;
                                WriteDetail($"✓ Found: '{titleStr}' by {artistName} (ID: {trackId})");
                                break;
                            }
                        }
                    }
                    
                    if (!foundTaylorSwift22)
                    {
                        WriteDetail("⚠ Could not find exact match for '22' by Taylor Swift in results");
                    }
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