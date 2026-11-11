namespace allstarr.Core.ClientApiV2;

/// <summary>Strict readers for list-style query values, shared by the routes and the batch call.</summary>
public static class V2Query
{
    /// <summary>Reads a comma-separated list of ids; false when it is empty, too long or has an empty entry.</summary>
    public static bool TryIds(string? value, out string[] ids)
    {
        ids = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        return ids.Length is > 0 and <= V2Contract.MaximumIdsPerRequest && ids.All(id => id.Length > 0);
    }

    /// <summary>Reads a comma-separated set of names; an unknown or repeated name is an error so a typo never looks like missing data.</summary>
    public static bool TryNames(string? value, IReadOnlySet<string> allowed, out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var name in value.Split(',', StringSplitOptions.TrimEntries))
            if (!allowed.Contains(name) || !names.Add(name)) return false;
        return true;
    }
}
