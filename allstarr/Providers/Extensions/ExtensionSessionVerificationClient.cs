using System.Net.Http.Json;
using System.Text.Json;

namespace allstarr.Core.Extensions;

public sealed class ExtensionSessionVerificationClient(IHttpClientFactory clients, IConfiguration configuration)
{
    public const string HttpClientName = "ExtensionSessionVerification";
    private readonly Uri? _endpoint = Endpoint(configuration["Extensions:SessionVerification:BaseUrl"]);
    public bool Enabled => _endpoint != null;

    internal static bool Supports(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("api.zarz.moe", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    internal async Task<string> VerifyAsync(ExtensionSessionChallenge challenge, CancellationToken cancellationToken)
    {
        if (_endpoint == null || !Supports(challenge.VerificationUrl))
            throw new InvalidOperationException("Automatic verification is not configured for this service.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new { verification_url = challenge.VerificationUrl })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 16 * 1024)
            throw new HttpRequestException("Automatic verification is temporarily unavailable.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (body.Length + read > 16 * 1024) throw new InvalidDataException("Verification response exceeds its limit.");
            body.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(body.ToArray());
        if (!document.RootElement.TryGetProperty("callback_url", out var callback) || callback.ValueKind != JsonValueKind.String ||
            callback.GetString() is not { Length: > 0 and <= 8192 } value)
            throw new InvalidDataException("Verification did not return a callback.");
        return value;
    }

    private static Uri? Endpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/api/verify") : null;
}
