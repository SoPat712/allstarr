using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Capabilities;

public static class ProviderAccountSettings
{
    public const string Empty = "{}";
    private const int MaximumJsonLength = 8192;

    public static string Project(ProviderDescriptor provider, ReadOnlyMemory<byte> configuration)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var secretKeys = provider.Permissions.SecretSettingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = provider.Settings
            .Where(item => item.ValueKind != ProviderSettingValueKind.Secret && !secretKeys.Contains(item.Key))
            .Select(item => item.Key)
            .ToArray();
        if (keys.Length == 0) return Empty;
        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            var comparison = provider.Origin == ProviderOrigin.BuiltIn
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var property = document.RootElement.EnumerateObject()
                    .FirstOrDefault(item => item.Name.Equals(key, comparison));
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or
                    JsonValueKind.True or JsonValueKind.False)
                    values[key] = property.Value.Clone();
            }
            var json = JsonSerializer.Serialize(values);
            return json.Length <= MaximumJsonLength ? json : Empty;
        }
        catch (JsonException) { return Empty; }
    }

    public static string? ReadText(string settingsJson, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(key, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

public interface IProviderAccountSettingsReader
{
    Task<string?> GetTextAsync(ProviderAccountContext? account, string key, CancellationToken cancellationToken);
}

public sealed class ProviderAccountSettingsReader(IDbContextFactory<AllstarrDbContext> contextFactory)
    : IProviderAccountSettingsReader
{
    private const int MaximumCachedAccounts = 1024;
    private readonly ConcurrentDictionary<(Guid AccountId, long Revision), string> _cache = new();

    public async Task<string?> GetTextAsync(
        ProviderAccountContext? account,
        string key,
        CancellationToken cancellationToken)
    {
        if (account == null) return null;
        if (!_cache.TryGetValue((account.AccountId, account.Revision), out var settings))
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await context.ProviderAccounts.AsNoTracking()
                .Where(item => item.Id == account.AccountId && item.Revision == account.Revision)
                .Select(item => new { item.SettingsJson })
                .SingleOrDefaultAsync(cancellationToken);
            if (row == null) return null;
            if (_cache.Count >= MaximumCachedAccounts) _cache.Clear();
            settings = _cache[(account.AccountId, account.Revision)] = row.SettingsJson;
        }
        return ProviderAccountSettings.ReadText(settings, key);
    }
}
