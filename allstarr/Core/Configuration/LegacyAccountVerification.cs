using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Core.Configuration;

public sealed record LegacyAccountCheck(string ProviderId, bool Enabled, string Reason);

public interface ILegacyAccountVerifier
{
    Task<bool> VerifyAsync(string providerId, Guid accountId, ReadOnlyMemory<byte> credential, CancellationToken token);
}

public sealed class LegacyAccountVerifier(ProviderStatusManager status) : ILegacyAccountVerifier
{
    public async Task<bool> VerifyAsync(string providerId, Guid accountId, ReadOnlyMemory<byte> credential, CancellationToken token)
    {
        using var document = JsonDocument.Parse(credential);
        var fields = document.RootElement.EnumerateObject()
            .Where(item => item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .ToDictionary(item => new string(item.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                item => item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() ?? "" : item.Value.GetRawText(),
                StringComparer.Ordinal);
        return await status.TestManagedProviderConnectionAsync(providerId, accountId, fields, token, accountRevision: 0);
    }
}

public sealed partial class LegacyEnvMigrationService
{
    private sealed record CheckedAccount(Guid Id, LegacyAccountCheck Check);

    private async Task<Dictionary<string, CheckedAccount>> CheckAccountsAsync(PreviewState state, CancellationToken token)
    {
        var providers = state.ProviderAccounts.Where(item => item.Action == "create_disabled_if_missing").Select(item => item.ProviderId)
            .Concat(state.Items.Where(item => item.Action == "import_for_current_user").Select(item => item.ProviderId!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, CheckedAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var id = Guid.CreateVersion7();
            var healthy = false;
            var bytes = provider is "lastfm" or "listenbrainz" ? BuildPersonalProviderSecret(state.Document, provider) :
                BuildProviderSecret(state.Document, provider);
            try
            {
                if (_accountVerifier != null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    try { healthy = await _accountVerifier.VerifyAsync(provider, id, bytes, deadline.Token); }
                    catch (Exception error) when (!token.IsCancellationRequested && error is not OutOfMemoryException) { }
                }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            result.Add(provider, new(id, new(provider, healthy, healthy ? "Connection verified; account enabled." :
                "Connection could not be verified. Reconnect or test this account in Integrations, then enable it.")));
        }
        return result;
    }
}
