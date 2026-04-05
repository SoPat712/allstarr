using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace allstarr.Services.Common;

public static class ImageConditionalRequestHelper
{
    public static string ComputeStrongETag(byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        return $"\"{Convert.ToHexString(hash)}\"";
    }

    public static bool MatchesIfNoneMatch(IHeaderDictionary headers, string etag)
    {
        if (!headers.TryGetValue("If-None-Match", out var headerValues))
        {
            return false;
        }

        foreach (var headerValue in headerValues)
        {
            if (string.IsNullOrEmpty(headerValue))
            {
                continue;
            }

            foreach (var candidate in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
