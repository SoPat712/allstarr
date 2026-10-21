namespace allstarr.Core.Providers.Lyrics;

internal static class MusixmatchMobileApi
{
    internal const string Host = "apic-appmobile.musixmatch.com";
    internal const string AppId = "mac-ios-v2.0";
    internal const string AppVersion = "10.1.1";
    internal const string AppBuild = "2025120901";
    internal const string NetworkVersion = "CFNetwork/3860.300.31 Darwin/25.2.0";
    internal const string Cookie = "x-mxm-token-guid=";
    internal const string AcceptLanguage = "en-US,en;q=0.9";
    internal const string Accept = "application/json";
    internal static readonly Uri LyricsEndpoint = new($"https://{Host}/ws/1.1/macro.subtitles.get");
    internal static readonly Uri TokenEndpoint = new($"https://{Host}/ws/1.1/token.get?app_id={AppId}");
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    internal static HttpRequestMessage CreateRequest(Uri uri, MusixmatchCredential? credential = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Host = Host;
        request.Headers.Add("authority", Host);
        request.Headers.Add("Cookie", Cookie);
        request.Headers.Add("x-mxm-app-version", credential?.AppVersion ?? AppVersion);
        request.Headers.UserAgent.ParseAdd($"Musixmatch/{credential?.AppBuild ?? AppBuild} {NetworkVersion}");
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        request.Headers.Accept.ParseAdd(Accept);
        request.Headers.Connection.Add("keep-alive");
        return request;
    }
}
