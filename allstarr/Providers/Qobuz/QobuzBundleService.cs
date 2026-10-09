using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace allstarr.Core.Providers.Qobuz;

public sealed record QobuzSigningCredentials(string AppId, IReadOnlyList<string> Secrets);

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

    private QobuzSigningCredentials? _cached;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public QobuzBundleService(IHttpClientFactory httpClientFactory, ILogger<QobuzBundleService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("QobuzApi");
        _logger = logger;
    }

    public virtual Task<string> GetAppIdAsync() => GetAppIdAsync(CancellationToken.None);

    public virtual async Task<string> GetAppIdAsync(CancellationToken cancellationToken)
    {
        var snapshot = await EnsureInitializedAsync(cancellationToken);
        return snapshot.AppId;
    }

    public virtual Task<List<string>> GetSecretsAsync() => GetSecretsAsync(CancellationToken.None);

    public virtual Task<QobuzSigningCredentials> GetSigningCredentialsAsync(CancellationToken cancellationToken) =>
        EnsureInitializedAsync(cancellationToken);

    public virtual async Task<List<string>> GetSecretsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await EnsureInitializedAsync(cancellationToken);
        return snapshot.Secrets.ToList();
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

    public virtual async Task RefreshAsync(string expectedAppId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAppId);
        cancellationToken.ThrowIfCancellationRequested();
        var observed = Volatile.Read(ref _cached);
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _cached);
            if (current != null && (!ReferenceEquals(current, observed) ||
                !string.Equals(current.AppId, expectedAppId, StringComparison.Ordinal))) return;
            var refreshed = await FetchSnapshotAsync(cancellationToken);
            Volatile.Write(ref _cached, refreshed);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<QobuzSigningCredentials> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _cached);
        if (snapshot != null) return snapshot;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            snapshot = Volatile.Read(ref _cached);
            if (snapshot != null) return snapshot;
            snapshot = await FetchSnapshotAsync(cancellationToken);
            Volatile.Write(ref _cached, snapshot);
            return snapshot;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<QobuzSigningCredentials> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        var bundleUrl = await GetBundleUrlAsync(cancellationToken);
        var bundleJs = await DownloadBundleAsync(bundleUrl, cancellationToken);
        var appId = ExtractAppId(bundleJs);
        var secrets = ExtractSecrets(bundleJs);
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Qobuz signing bundle loaded successfully.");
        return new QobuzSigningCredentials(appId, Array.AsReadOnly(secrets.ToArray()));
    }

    private async Task<string> GetBundleUrlAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync(LoginPageUrl, cancellationToken);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = BundleUrlRegex.Match(html);

        if (!match.Success)
        {
            throw InvalidBundle();
        }

        return BaseUrl + match.Groups[1].Value;
    }

    private async Task<string> DownloadBundleAsync(string bundleUrl, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(bundleUrl, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string ExtractAppId(string bundleJs)
    {
        var match = AppIdRegex.Match(bundleJs);

        if (!match.Success)
        {
            throw InvalidBundle();
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
            throw InvalidBundle();
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
            if (kvp.Value.Count < 3) continue;
            var concatenated = string.Join("", kvp.Value);
            if (concatenated.Length <= 44) continue;
            concatenated = concatenated.Substring(0, concatenated.Length - 44);

            try
            {
                var bytes = Convert.FromBase64String(concatenated);
                var decoded = new UTF8Encoding(false, true).GetString(bytes);
                if (!string.IsNullOrWhiteSpace(decoded)) decodedSecrets.Add(decoded);
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                _logger.LogDebug("A Qobuz signing candidate could not be decoded.");
            }
        }

        if (decodedSecrets.Count == 0)
        {
            throw InvalidBundle();
        }

        return decodedSecrets;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException("Qobuz signing bundle request failed.", null, status);
    }

    private static HttpRequestException InvalidBundle() =>
        new("Qobuz signing bundle is unavailable or invalid.", null, HttpStatusCode.BadGateway);
}
