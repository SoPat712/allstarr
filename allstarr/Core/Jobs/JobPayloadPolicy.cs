using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Jobs;

public sealed partial class JobPayloadPolicy
{
    private static readonly string[] SensitiveNameFragments =
    [
        "password",
        "passphrase",
        "credential",
        "secret",
        "token",
        "cookie",
        "authorization",
        "bearer",
        "apikey",
        "privatekey",
        "clientsecret",
        "sessionkey",
        "sessiontoken",
        "sessioncookie",
        "arl",
        "spdc"
    ];

    private readonly DurableJobOptions _options;

    public JobPayloadPolicy(DurableJobOptions options)
    {
        _options = options;
    }

    public string SerializeAndValidate<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Job payload exceeds {_options.MaxPayloadBytes} bytes.");
        }

        using var document = JsonDocument.Parse(json);
        ValidateElement(document.RootElement, "$");
        return Canonicalize(document.RootElement);
    }

    private static void ValidateElement(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitiveFieldName(property.Name))
                    {
                        throw new InvalidOperationException(
                            $"Job payload field '{path}.{property.Name}' may contain a secret; store a secret reference ID instead.");
                    }

                    ValidateElement(property.Value, $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ValidateElement(item, $"{path}[{index++}]");
                }

                break;
            case JsonValueKind.String:
                if (LooksLikeEmbeddedSecret(element.GetString()))
                {
                    throw new InvalidOperationException(
                        $"Job payload value at '{path}' may contain an embedded credential or secret-bearing URL; store a secret reference ID instead.");
                }

                break;
        }
    }

    private static bool IsSensitiveFieldName(string name)
    {
        var normalized = NormalizeName(name);
        if (normalized.Contains("reference", StringComparison.Ordinal) &&
            (normalized.EndsWith("id", StringComparison.Ordinal) ||
             normalized.EndsWith("key", StringComparison.Ordinal)))
        {
            return false;
        }

        return SensitiveNameFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static bool LooksLikeEmbeddedSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var decoded = value.Trim();
        try
        {
            decoded = Uri.UnescapeDataString(decoded);
        }
        catch (UriFormatException)
        {
            // Inspect the original text when malformed percent escapes prevent decoding.
        }

        if (BearerValuePattern().IsMatch(decoded) || LabeledSecretPattern().IsMatch(decoded))
        {
            return true;
        }

        return Uri.TryCreate(decoded, UriKind.Absolute, out var uri) &&
               !string.IsNullOrEmpty(uri.UserInfo);
    }

    private static string NormalizeName(string value) => new(
        value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    [GeneratedRegex(
        @"(?i)(?:^|[?&#;,\s])[^=;,&\s]*(?:password|passphrase|credential|secret|token|cookie|authorization|api[_-]?key|private[_-]?key|arl|session[_-]?(?:key|token|cookie)|sp_dc)[^=;,&\s]*\s*=\s*[^\s,;&]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex LabeledSecretPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerValuePattern();
}
