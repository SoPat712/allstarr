using System.Text.RegularExpressions;

namespace allstarr.Core.Capabilities;

internal static partial class ProviderContractValidation
{
    private const int MaximumIdentifierLength = 100;

    public static string ProviderId(string value, string parameterName)
    {
        var candidate = RequiredText(value, parameterName, MaximumIdentifierLength);
        if (!ProviderIdPattern().IsMatch(candidate))
        {
            throw new ArgumentException(
                "Provider IDs must use stable lowercase kebab-case.",
                parameterName);
        }

        return candidate;
    }

    public static string Catalog(string value, string parameterName)
    {
        var candidate = RequiredText(value, parameterName, MaximumIdentifierLength);
        if (!CatalogPattern().IsMatch(candidate))
        {
            throw new ArgumentException(
                "Provider catalogs must use lowercase letters, digits, dots, underscores, or hyphens.",
                parameterName);
        }

        return candidate;
    }

    public static string HookName(string value, string parameterName)
    {
        var candidate = RequiredText(value, parameterName, MaximumIdentifierLength);
        if (!HookNamePattern().IsMatch(candidate))
        {
            throw new ArgumentException("Capability hook names must use lower camel case.", parameterName);
        }

        return candidate;
    }

    public static string SettingKey(string value, string parameterName)
    {
        var candidate = RequiredText(value, parameterName, MaximumIdentifierLength);
        if (!SettingKeyPattern().IsMatch(candidate))
        {
            throw new ArgumentException(
                "Setting keys must start with a lowercase letter and contain only letters or digits.",
                parameterName);
        }

        return candidate;
    }

    public static string RequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, trimmed, control-character-free, and at most {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    public static string? OptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value == null)
        {
            return null;
        }

        return RequiredText(value, parameterName, maximumLength);
    }

    public static string SafeMessage(string value, string parameterName)
    {
        var candidate = RequiredText(value, parameterName, 300);
        var normalized = candidate.ToLowerInvariant();
        string[] forbiddenFragments =
        [
            "://",
            "authorization:",
            "bearer ",
            "cookie:",
            "password=",
            "secret=",
            "token="
        ];
        if (candidate.StartsWith('{') ||
            candidate.StartsWith('[') ||
            forbiddenFragments.Any(normalized.Contains))
        {
            throw new ArgumentException(
                "Provider error messages must be redacted, host-authored text.",
                parameterName);
        }

        return candidate;
    }

    public static string? OptionalContent(string? value, string parameterName, int maximumLength)
    {
        if (value == null)
        {
            return null;
        }

        if (value.Length == 0 ||
            value.Length > maximumLength ||
            value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, contain only display-safe controls, and be at most {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    public static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) =>
        Array.AsReadOnly((values ?? []).ToArray());

    public static IReadOnlyList<T> CopyDistinct<T>(
        IEnumerable<T>? values,
        string parameterName,
        IEqualityComparer<T>? comparer = null)
    {
        var copy = (values ?? []).ToArray();
        var distinct = new HashSet<T>(comparer);
        if (copy.Any(item => !distinct.Add(item)))
        {
            throw new ArgumentException($"{parameterName} cannot contain duplicates.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HookNamePattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();
}
