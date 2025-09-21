using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models;

namespace octo_fiesta.Controllers;

[ApiController]
[Route("")]
public class SubsonicController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    public SubsonicController(IHttpClientFactory httpClientFactory, IOptions<SubsonicSettings> subsonicSettings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;

        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new Exception("Error: Environment variable SUBSONIC_URL is not set.");
        }
    }

    // Extract all parameters (query + body)
    private async Task<Dictionary<string, string>> ExtractAllParameters()
    {
        var parameters = new Dictionary<string, string>();

        // Get query parameters
        foreach (var query in Request.Query)
        {
            parameters[query.Key] = query.Value.ToString();
        }

        // Get body parameters (JSON)
        if (Request.ContentLength > 0 && Request.ContentType?.Contains("application/json") == true)
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var bodyParams = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                    foreach (var param in bodyParams)
                    {
                        parameters[param.Key] = param.Value?.ToString() ?? "";
                    }
                }
                catch (JsonException)
                {
                    
                }
            }
        }

        return parameters;
    }

    private async Task<(string Body, string? ContentType)> RelayToSubsonic(string endpoint, Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url}/{endpoint}?{query}";
        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        return (body, contentType);
    }

    [HttpGet, HttpPost]
    [Route("ping")]
    public async Task<IActionResult> Ping()
    {
        var parameters = await ExtractAllParameters();
        var xml = await RelayToSubsonic("ping", parameters);
        var doc = XDocument.Parse(xml.Body);
        var status = doc.Root?.Attribute("status")?.Value;
        return Ok(new { status });
    }

    // Generic endpoint to handle all subsonic API calls
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        try
        {
            var result = await RelayToSubsonic(endpoint, parameters);
            var contentType = result.ContentType ?? $"application/{parameters.GetValueOrDefault("f", "xml")}";
            return Content(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new { error = $"Error while calling Subsonic: {ex.Message}" });
        }
    }
}