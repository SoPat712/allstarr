using allstarr.Services.Validation;
using allstarr.Services.Common;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup (no auth needed)
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private readonly List<string> _apiUrls;
    private readonly List<string> _streamingUrls;
    private readonly RoundRobinFallbackHelper _apiFallbackHelper;
    private readonly RoundRobinFallbackHelper _streamingFallbackHelper;
    private readonly EndpointBenchmarkService _benchmarkService;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(
        HttpClient httpClient,
        List<string> apiUrls,
        List<string> streamingUrls,
        EndpointBenchmarkService benchmarkService,
        ILogger<SquidWTFStartupValidator> logger)
        : base(httpClient)
    {
        _apiUrls = apiUrls;
        _streamingUrls = streamingUrls;
        _apiFallbackHelper = new RoundRobinFallbackHelper(_apiUrls, logger, "SquidWTF API");
        _streamingFallbackHelper = new RoundRobinFallbackHelper(_streamingUrls, logger, "SquidWTF Streaming");
        _benchmarkService = benchmarkService;
    }


    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        await BenchmarkEndpointPoolAsync(_apiUrls, _apiFallbackHelper, cancellationToken);
        await BenchmarkEndpointPoolAsync(_streamingUrls, _streamingFallbackHelper, cancellationToken);

        if (_apiUrls.Count == 0)
        {
            return ValidationResult.Failure(
                "UNAVAILABLE",
                "SquidWTF uptime feeds did not return any usable API endpoints",
                ConsoleColor.Yellow);
        }

        var apiResult = await _apiFallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
            {
                var response = await _httpClient.GetAsync(baseUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
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

        if (_streamingUrls.Count > 0)
        {
            var streamingResult = await _streamingFallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
            {
                var response = await _httpClient.GetAsync(baseUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ValidationResult.Success("SquidWTF streaming endpoint validation completed");
                }

                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }, ValidationResult.Failure("-2", "All SquidWTF streaming endpoints failed"));

            if (!streamingResult.IsValid)
            {
                return streamingResult;
            }
        }

        return ValidationResult.Success("SquidWTF API validation completed");
    }

    private async Task BenchmarkEndpointPoolAsync(
        List<string> endpoints,
        RoundRobinFallbackHelper fallbackHelper,
        CancellationToken cancellationToken)
    {
        if (endpoints.Count <= 1)
        {
            return;
        }


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
            return;
        }

        fallbackHelper.SetEndpointOrder(orderedEndpoints);
    }
}
