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
    private readonly List<string> _apiUrls;
    private readonly List<string> _streamingUrls;
    private readonly RoundRobinFallbackHelper _apiFallbackHelper;
    private readonly RoundRobinFallbackHelper _streamingFallbackHelper;
    private readonly EndpointBenchmarkService _benchmarkService;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(
        IOptions<SquidWTFSettings> settings, 
        HttpClient httpClient, 
        List<string> apiUrls,
        List<string> streamingUrls,
        EndpointBenchmarkService benchmarkService,
        ILogger<SquidWTFStartupValidator> logger)
        : base(httpClient)
    {
        _settings = settings.Value;
        _apiUrls = apiUrls;
        _streamingUrls = streamingUrls;
        _apiFallbackHelper = new RoundRobinFallbackHelper(_apiUrls, logger, "SquidWTF API");
        _streamingFallbackHelper = new RoundRobinFallbackHelper(_streamingUrls, logger, "SquidWTF Streaming");
        _benchmarkService = benchmarkService;
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

        WriteStatus("SquidWTF API Endpoints", _apiUrls.Count.ToString(), ConsoleColor.Cyan);
        WriteStatus("SquidWTF Streaming Endpoints", _streamingUrls.Count.ToString(), ConsoleColor.Cyan);

        await BenchmarkEndpointPoolAsync("API", _apiUrls, _apiFallbackHelper, cancellationToken);
        await BenchmarkEndpointPoolAsync("streaming", _streamingUrls, _streamingFallbackHelper, cancellationToken);

        // Validate API endpoints and search functionality.
        var apiResult = await _apiFallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
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
        }, ValidationResult.Failure("-1", "All SquidWTF API endpoints failed"));

        if (!apiResult.IsValid)
        {
            return apiResult;
        }

        // Validate streaming endpoints independently to avoid API-only endpoints for streaming.
        var streamingResult = await _streamingFallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            var response = await _httpClient.GetAsync(baseUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF Streaming", $"REACHABLE ({baseUrl})", ConsoleColor.Green);
                return ValidationResult.Success("SquidWTF streaming endpoint validation completed");
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }, ValidationResult.Failure("-2", "All SquidWTF streaming endpoints failed"));

        if (!streamingResult.IsValid)
        {
            return streamingResult;
        }

        return ValidationResult.Success("SquidWTF API and streaming validation completed");
    }

    private async Task BenchmarkEndpointPoolAsync(
        string poolName,
        List<string> endpoints,
        RoundRobinFallbackHelper fallbackHelper,
        CancellationToken cancellationToken)
    {
        if (endpoints.Count <= 1)
        {
            return;
        }

        WriteStatus($"Benchmarking {poolName} endpoints", $"{endpoints.Count} endpoints", ConsoleColor.Cyan);

        var orderedEndpoints = await _benchmarkService.BenchmarkEndpointsAsync(
            endpoints,
            async (endpoint, ct) =>
            {
                try
                {
                    // 5 second timeout per ping - mark slow endpoints as failed.
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

        if (orderedEndpoints.Count == 0)
        {
            WriteDetail($"No healthy {poolName} endpoints detected during benchmark");
            return;
        }

        fallbackHelper.SetEndpointOrder(orderedEndpoints);

        var topEndpoints = orderedEndpoints.Take(5).ToList();
        WriteDetail($"Fastest {poolName} endpoint: {topEndpoints.First()}");

        if (topEndpoints.Count > 1)
        {
            WriteDetail($"Top {topEndpoints.Count} {poolName} endpoints by average latency:");
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
