using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Filters;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/applemusic")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AppleMusicController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly AppleMusicSettings _settings;
    private readonly ILogger<AppleMusicController> _logger;

    public AppleMusicController(
        IHttpClientFactory httpClientFactory,
        IOptions<AppleMusicSettings> settings,
        ILogger<AppleMusicController> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AppleMusic");
        _settings = settings.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromMinutes(10); // High timeout for file uploads & setup
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var res = await _httpClient.GetAsync("api/health");
            if (!res.IsSuccessStatusCode)
            {
                return StatusCode((int)res.StatusCode, await res.Content.ReadAsStringAsync());
            }

            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return Ok(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Apple Music sidecar");
            return StatusCode(503, new { error = "Apple Music sidecar container is offline or unreachable." });
        }
    }

    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded" });
        }

        try
        {
            using var content = new MultipartFormDataContent();
            using var fileStream = file.OpenReadStream();
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
            content.Add(streamContent, "file", file.FileName);

            _logger.LogInformation("Forwarding Apple Music APK/APKM setup upload to sidecar...");
            var res = await _httpClient.PostAsync("api/setup", content);
            
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError("Setup failed on sidecar: {Message}", json);
                return StatusCode((int)res.StatusCode, json);
            }

            using var doc = JsonDocument.Parse(json);
            return Ok(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup proxy call failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] JsonElement credentials)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync("api/login", credentials);
            var json = await res.Content.ReadAsStringAsync();
            
            if (res.StatusCode == System.Net.HttpStatusCode.Accepted || res.StatusCode == System.Net.HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(json);
                return StatusCode((int)res.StatusCode, doc.RootElement);
            }

            return StatusCode((int)res.StatusCode, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login proxy call failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("login/2fa")]
    public async Task<IActionResult> Login2fa([FromBody] JsonElement code)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync("api/login/2fa", code);
            var json = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                return Ok(doc.RootElement);
            }

            return StatusCode((int)res.StatusCode, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "2FA verification proxy call failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
