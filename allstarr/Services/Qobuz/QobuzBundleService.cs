using System.Text.RegularExpressions;

namespace allstarr.Services.Qobuz;

// Qobuz rotates these values; derive them from the bundle with qobuz-dl-compatible decoding.
public class QobuzBundleService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QobuzBundleService> _logger;

    private const string BaseUrl = "https://play.qobuz.com";
    private const string LoginPageUrl = "https://play.qobuz.com/login";

    private static readonly Regex BundleUrlRegex = new(
        @"<script src=""(/resources/\d+\.\d+\.\d+-[a-z]\d{3}/bundle\.js)""></script>",
        RegexOptions.Compiled);

    private static readonly Regex AppIdRegex = new(
        @"production:\{api:\{appId:""(?<app_id>\d{9})"",appSecret:""\w{32}""",
        RegexOptions.Compiled);

    private string? _cachedAppId;
    private List<string>? _cachedSecrets;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public QobuzBundleService(IHttpClientFactory httpClientFactory, ILogger<QobuzBundleService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        _logger = logger;
    }

    public virtual Task<string> GetAppIdAsync() => GetAppIdAsync(CancellationToken.None);

    public virtual async Task<string> GetAppIdAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _cachedAppId!;
    }

    public virtual Task<List<string>> GetSecretsAsync() => GetSecretsAsync(CancellationToken.None);

    public virtual async Task<List<string>> GetSecretsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _cachedSecrets!;
    }

    public virtual async Task<string> GetSecretAsync(int index = 0)
    {
        var secrets = await GetSecretsAsync();
        if (index < 0 || index >= secrets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Secret index {index} out of range (0-{secrets.Count - 1})");
        }
        return secrets[index];
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_cachedAppId != null && _cachedSecrets != null)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedAppId != null && _cachedSecrets != null)
            {
                return;
            }

            _logger.LogInformation("Extracting Qobuz App ID and secrets from web bundle...");

            var bundleUrl = await GetBundleUrlAsync(cancellationToken);
            _logger.LogDebug("Found bundle URL: {BundleUrl}", bundleUrl);

            var bundleJs = await DownloadBundleAsync(bundleUrl, cancellationToken);

            _cachedAppId = ExtractAppId(bundleJs);
            _logger.LogDebug("Extracted App ID: {AppId}", _cachedAppId);

            _cachedSecrets = ExtractSecrets(bundleJs);
            _logger.LogDebug("Extracted {Count} secrets", _cachedSecrets.Count);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> GetBundleUrlAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(LoginPageUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = BundleUrlRegex.Match(html);

        if (!match.Success)
        {
            throw new Exception("Could not find bundle URL in Qobuz login page");
        }

        return BaseUrl + match.Groups[1].Value;
    }

    private async Task<string> DownloadBundleAsync(string bundleUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(bundleUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string ExtractAppId(string bundleJs)
    {
        var match = AppIdRegex.Match(bundleJs);

        if (!match.Success)
        {
            throw new Exception("Could not extract App ID from bundle");
        }

        return match.Groups["app_id"].Value;
    }

    private List<string> ExtractSecrets(string bundleJs)
    {
        var secrets = new Dictionary<string, List<string>>();

        // Match seed/timezone pairs emitted by Qobuz's minified web bundle.
        var seedTimezonePattern = new Regex(
            @"[a-z]\.initialSeed\(""(?<seed>[\w=]+)"",window\.utimezone\.(?<timezone>[a-z]+)\)",
            RegexOptions.IgnoreCase);

        var seedMatches = seedTimezonePattern.Matches(bundleJs);

        foreach (Match match in seedMatches)
        {
            var seed = match.Groups["seed"].Value;
            var timezone = match.Groups["timezone"].Value.ToLower();

            if (!secrets.ContainsKey(timezone))
            {
                secrets[timezone] = new List<string>();
            }
            secrets[timezone].Add(seed);
        }

        if (secrets.Count == 0)
        {
            throw new Exception("Could not extract seed/timezone pairs from bundle");
        }

        // qobuz-dl moves the second timezone entry first before decoding.
        var keypairs = secrets.ToList();
        if (keypairs.Count > 1)
        {
            var secondItem = keypairs[1];
            secrets.Remove(secondItem.Key);
            var newDict = new Dictionary<string, List<string>> { { secondItem.Key, secondItem.Value } };
            foreach (var kv in keypairs)
            {
                if (kv.Key != secondItem.Key)
                {
                    newDict[kv.Key] = kv.Value;
                }
            }
            secrets = newDict;
        }

        // Each timezone contributes its seed plus matching info and extras fields.
        var timezones = string.Join("|", secrets.Keys.Select(tz =>
            char.ToUpper(tz[0]) + tz.Substring(1)));

        var infoExtrasPattern = new Regex(
            $@"name:""\w+/(?<timezone>{timezones})"",info:""(?<info>[\w=]+)"",extras:""(?<extras>[\w=]+)""",
            RegexOptions.IgnoreCase);

        var infoExtrasMatches = infoExtrasPattern.Matches(bundleJs);

        foreach (Match match in infoExtrasMatches)
        {
            var timezone = match.Groups["timezone"].Value.ToLower();
            var info = match.Groups["info"].Value;
            var extras = match.Groups["extras"].Value;

            if (secrets.ContainsKey(timezone))
            {
                secrets[timezone].Add(info);
                secrets[timezone].Add(extras);
            }
        }

        // qobuz-dl removes the final 44 characters from each concatenated payload.
        var decodedSecrets = new List<string>();

        foreach (var kvp in secrets)
        {
            var concatenated = string.Join("", kvp.Value);

            if (concatenated.Length > 44)
            {
                concatenated = concatenated.Substring(0, concatenated.Length - 44);
            }

            try
            {
                var bytes = Convert.FromBase64String(concatenated);
                var decoded = System.Text.Encoding.UTF8.GetString(bytes);
                decodedSecrets.Add(decoded);
                _logger.LogDebug("Decoded secret for timezone {Timezone}: {Length} chars", kvp.Key, decoded.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decode secret for timezone {Timezone}", kvp.Key);
            }
        }

        if (decodedSecrets.Count == 0)
        {
            throw new Exception("Could not decode any secrets from bundle");
        }

        return decodedSecrets;
    }

}
