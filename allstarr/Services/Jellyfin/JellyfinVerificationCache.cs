using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Jellyfin;

internal sealed class JellyfinVerificationCache(TimeProvider clock) : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly MemoryCache entries = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly object sync = new();

    internal sealed record Verification(string? PrincipalId, string? DisplayName, bool IsAdministrator,
        AllstarrPrincipal? Principal, DateTimeOffset VerifiedAt);

    internal sealed class Credential(string[] credentialHashes)
    {
        internal readonly string[] CredentialHashes = credentialHashes;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<string, Verification> Paths = new(StringComparer.Ordinal);
    }

    internal Credential Get(IHeaderDictionary headers, string? query)
    {
        var (token, queryToken) = Tokens(headers, query);
        var key = MaterialKey(token, queryToken);
        lock (sync)
        {
            if (entries.TryGetValue<Credential>(key, out var credential)) return credential!;
            credential = new Credential(CandidateHashes(token, queryToken));
            entries.Set(key, credential, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = Lifetime });
            return credential;
        }
    }

    internal Verification? Find(Credential credential, string path) =>
        credential.Paths.TryGetValue(path, out var value) && clock.GetUtcNow() - value.VerifiedAt < Lifetime
            ? value : null;

    internal DateTimeOffset UtcNow => clock.GetUtcNow();

    internal void Evict(IHeaderDictionary headers, string? query)
    {
        var (token, queryToken) = Tokens(headers, query);
        var hashes = CandidateHashes(token, queryToken);
        lock (sync)
        {
            foreach (var key in entries.Keys)
            {
                if (entries.TryGetValue<Credential>(key, out var credential) &&
                    credential!.CredentialHashes.Any(hash => hashes.Contains(hash, StringComparer.Ordinal)))
                    entries.Remove(key);
            }
        }
    }

    internal static string CredentialKey(IHeaderDictionary headers, string? query)
    {
        var (token, queryToken) = Tokens(headers, query);
        return MaterialKey(token, queryToken);
    }

    private static string[] CandidateHashes(string? token, string? queryToken) =>
        new[] { token, queryToken }.Where(value => value != null).Distinct(StringComparer.Ordinal)
            .Select(value => MaterialKey(value, null)).ToArray();

    private static (string? Token, string? QueryToken) Tokens(IHeaderDictionary headers, string? query)
    {
        // Match the forwarded header's precedence, including ambiguous header/query combinations.
        var authorization = AuthHeaderHelper.BuildForwardedAuthorization(headers);
        var forwarded = new HeaderDictionary();
        if (authorization != null) forwarded["Authorization"] = authorization;
        var token = AuthHeaderHelper.ExtractToken(forwarded);
        if (token == null && authorization?.StartsWith("MediaBrowser ", StringComparison.OrdinalIgnoreCase) != true)
            token = authorization;
        var normalized = JellyfinProxyService.NormalizeQueryCredentials("/" + query);
        var parameters = QueryHelpers.ParseQuery(normalized.Contains('?') ? normalized[normalized.IndexOf('?')..] : "");
        var queryToken = parameters.TryGetValue("ApiKey", out var value) ? value.ToString() : null;
        return (token, queryToken);
    }

    private static string MaterialKey(string? token, string? queryToken)
    {
        var material = token == null || queryToken == null || token == queryToken
            ? JsonSerializer.Serialize(token ?? queryToken)
            : JsonSerializer.Serialize(new[] { token, queryToken });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public void Dispose() => entries.Dispose();
}
