using System.Collections;
using System.Text.Json;

namespace allstarr.Services.Subsonic;

public enum SubsonicParameterSource
{
    Query,
    Form,
    Json
}

public sealed record SubsonicParameter(
    string Name,
    string Value,
    SubsonicParameterSource Source);

/// <summary>
/// Preserves the inbound method, parameter source, repetition, and ordering.
/// </summary>
public sealed class SubsonicRequestParameters : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyList<SubsonicParameter> _parameters;

    public SubsonicRequestParameters(
        string method,
        string? contentType,
        string? rawBody,
        IReadOnlyList<SubsonicParameter> parameters)
    {
        Method = method;
        ContentType = contentType;
        RawBody = rawBody;
        _parameters = parameters;
    }

    public string Method { get; }

    public string? ContentType { get; }

    public string? RawBody { get; }

    public IReadOnlyList<SubsonicParameter> Ordered => _parameters;

    public IEnumerable<SubsonicParameter> QueryParameters =>
        _parameters.Where(parameter => parameter.Source == SubsonicParameterSource.Query);

    public IEnumerable<SubsonicParameter> BodyParameters =>
        _parameters.Where(parameter => parameter.Source != SubsonicParameterSource.Query);

    public string this[string key] => TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => _parameters
        .Select(parameter => parameter.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Values => Keys.Select(key => this[key]);

    public int Count => Keys.Count();

    public bool ContainsKey(string key) =>
        _parameters.Any(parameter => parameter.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    public bool TryGetValue(string key, out string value)
    {
        var matching = _parameters
            .Where(parameter => parameter.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
        {
            value = string.Empty;
            return false;
        }

        var bodyValues = matching
            .Where(parameter => parameter.Source != SubsonicParameterSource.Query)
            .Select(parameter => parameter.Value)
            .ToList();
        var selected = bodyValues.Count > 0
            ? bodyValues
            : matching.Select(parameter => parameter.Value).ToList();

        // Preserve the previous scalar view while retaining each value in Ordered for relay.
        value = string.Join(',', selected);
        return true;
    }

    public string GetValueOrDefault(string key, string defaultValue = "") =>
        TryGetValue(key, out var value) ? value : defaultValue;

    public IReadOnlyList<string> GetAllValues(string key) => _parameters
        .Where(parameter => parameter.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(parameter => parameter.Value)
        .ToList();

    public bool HasNonEmptyValue(string key) =>
        GetAllValues(key).Any(value => !string.IsNullOrWhiteSpace(value));

    public SubsonicRequestParameters Select(IReadOnlySet<string> names)
    {
        var selected = _parameters
            .Where(parameter => names.Contains(parameter.Name))
            .ToList();
        string? body = null;

        if (selected.Any(parameter => parameter.Source == SubsonicParameterSource.Form))
        {
            body = EncodePairs(selected.Where(parameter => parameter.Source == SubsonicParameterSource.Form));
        }
        else if (selected.Any(parameter => parameter.Source == SubsonicParameterSource.Json))
        {
            body = JsonSerializer.Serialize(selected
                .Where(parameter => parameter.Source == SubsonicParameterSource.Json)
                .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count() == 1
                        ? (object)group.First().Value
                        : group.Select(parameter => parameter.Value).ToArray(),
                    StringComparer.OrdinalIgnoreCase));
        }

        return new SubsonicRequestParameters(Method, ContentType, body, selected);
    }

    public static SubsonicRequestParameters FromDictionary(
        IReadOnlyDictionary<string, string> parameters,
        string method = "GET")
    {
        return new SubsonicRequestParameters(
            method,
            contentType: null,
            rawBody: null,
            parameters.Select(parameter => new SubsonicParameter(
                parameter.Key,
                parameter.Value,
                SubsonicParameterSource.Query)).ToList());
    }

    public static string EncodePairs(IEnumerable<SubsonicParameter> parameters) => string.Join(
        '&',
        parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"));

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Keys
        .Select(key => new KeyValuePair<string, string>(key, this[key]))
        .GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
