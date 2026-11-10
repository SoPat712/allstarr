using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Subsonic;

public sealed class SubsonicVerificationCache(TimeProvider clock) : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    internal static readonly IReadOnlySet<string> ParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "t", "s", "apiKey", "v", "c", "f"
    };
    private static readonly IReadOnlySet<string> AuthenticationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "t", "s", "apiKey"
    };
    private readonly MemoryCache entries = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly object sync = new();

    internal sealed record Verification(string PrincipalName, AllstarrPrincipal? Principal, DateTimeOffset VerifiedAt);

    internal sealed class Credential(string authenticationHash)
    {
        internal readonly string AuthenticationHash = authenticationHash;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal Verification? Verified;
    }

    internal Credential Get(SubsonicRequestParameters parameters)
    {
        var key = CredentialKey(parameters);
        lock (sync)
        {
            if (entries.TryGetValue<Credential>(key, out var credential)) return credential!;
            credential = new Credential(AuthenticationKey(parameters));
            entries.Set(key, credential, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = Lifetime });
            return credential;
        }
    }

    internal Verification? Find(Credential credential) =>
        credential.Verified is { } value && clock.GetUtcNow() - value.VerifiedAt < Lifetime ? value : null;

    internal DateTimeOffset UtcNow => clock.GetUtcNow();

    internal void Evict(SubsonicRequestParameters parameters)
    {
        var hash = AuthenticationKey(parameters);
        lock (sync)
        {
            foreach (var key in entries.Keys)
                if (entries.TryGetValue<Credential>(key, out var credential) && credential!.AuthenticationHash == hash)
                {
                    credential.Verified = null;
                    entries.Remove(key);
                }
        }
    }

    internal static string CredentialKey(SubsonicRequestParameters parameters)
    {
        var selected = parameters.Select(ParameterNames);
        return Hash(JsonSerializer.Serialize(new
        {
            selected.Method,
            selected.ContentType,
            Query = SubsonicRequestParameters.EncodePairs(selected.QueryParameters),
            selected.RawBody
        }));
    }

    private static string AuthenticationKey(SubsonicRequestParameters parameters) => Hash(JsonSerializer.Serialize(
        parameters.Ordered.Where(parameter => AuthenticationNames.Contains(parameter.Name))
            .Select(parameter => new[] { parameter.Name.ToLowerInvariant(), parameter.Value })
            .OrderBy(pair => pair[0], StringComparer.Ordinal).ThenBy(pair => pair[1], StringComparer.Ordinal)));

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    internal static bool IsAuthenticationFailure(byte[] body)
    {
        var first = body.AsSpan();
        if (first.Length >= 3 && first[0] == 0xef && first[1] == 0xbb && first[2] == 0xbf) first = first[3..];
        while (!first.IsEmpty && first[0] is 32 or 13 or 10 or 9) first = first[1..];
        if (first.IsEmpty || first[0] is not (123 or 60)) return false;
        try
        {
            var content = Encoding.UTF8.GetString(first);
            if (content.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("subsonic-response", out var root) ||
                    !root.TryGetProperty("status", out var status) || status.GetString() != "failed" ||
                    !root.TryGetProperty("error", out var error) || !error.TryGetProperty("code", out var code)) return false;
                return int.TryParse(code.ToString(), out var value) && value is >= 40 and <= 44;
            }
            if (!content.StartsWith('<')) return false;
            var xml = XDocument.Parse(content).Root;
            return xml?.Name.LocalName == "subsonic-response" && xml.Attribute("status")?.Value == "failed" &&
                   int.TryParse(xml.Elements().FirstOrDefault(item => item.Name.LocalName == "error")?.Attribute("code")?.Value,
                       out var xmlCode) && xmlCode is >= 40 and <= 44;
        }
        catch (Exception error) when (error is JsonException or System.Xml.XmlException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose() => entries.Dispose();
}
