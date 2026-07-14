using System.Net;
using System.Text.Json;

namespace allstarr.Services.Subsonic;

/// <summary>
/// Service responsible for parsing HTTP request parameters from various sources
/// (query string, form body, JSON body) for Subsonic API requests.
/// </summary>
public class SubsonicRequestParser
{
    /// <summary>
    /// Extracts all parameters from an HTTP request (query parameters + body parameters).
    /// Supports multiple content types: application/x-www-form-urlencoded and application/json.
    /// </summary>
    /// <param name="request">The HTTP request to parse</param>
    /// <returns>Parameters with their original source and repetition preserved.</returns>
    public async Task<SubsonicRequestParameters> ExtractAllParametersAsync(HttpRequest request)
    {
        var parameters = new List<SubsonicParameter>();

        parameters.AddRange(ParseEncodedPairs(
            request.QueryString.Value?.TrimStart('?'),
            SubsonicParameterSource.Query));

        string? rawBody = null;

        if (request.ContentLength > 0 || request.ContentType != null)
        {
            request.EnableBuffering();
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }

            using var reader = new StreamReader(request.Body, leaveOpen: true);
            rawBody = await reader.ReadToEndAsync();
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }

            if (request.HasFormContentType)
            {
                parameters.AddRange(ParseEncodedPairs(rawBody, SubsonicParameterSource.Form));
            }
            else if (request.ContentType?.Contains("application/json") == true)
            {
                parameters.AddRange(ParseJsonParameters(rawBody));
            }
        }

        return new SubsonicRequestParameters(
            request.Method,
            request.ContentType,
            rawBody,
            parameters);
    }

    private static IEnumerable<SubsonicParameter> ParseEncodedPairs(
        string? encoded,
        SubsonicParameterSource source)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            yield break;
        }

        foreach (var part in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var name = WebUtility.UrlDecode(pair[0]);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var value = pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : string.Empty;
            yield return new SubsonicParameter(name, value, source);
        }
    }

    private static IEnumerable<SubsonicParameter> ParseJsonParameters(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        yield return new SubsonicParameter(
                            property.Name,
                            JsonScalarValue(item),
                            SubsonicParameterSource.Json);
                    }

                    continue;
                }

                yield return new SubsonicParameter(
                    property.Name,
                    JsonScalarValue(property.Value),
                    SubsonicParameterSource.Json);
            }
        }
    }

    private static string JsonScalarValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        _ => value.ToString()
    };
}
