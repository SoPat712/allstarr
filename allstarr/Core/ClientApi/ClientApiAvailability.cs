using allstarr.Core.Settings;

namespace allstarr.Core.ClientApi;

public sealed class ClientApiAvailability(IDurableRuntimeSettings settings)
{
    public const string SettingKey = "ClientApi:Enabled";
    private const string RequestItemKey = "allstarr.client-api-enabled";

    public async Task<bool> IsEnabledAsync(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestItemKey, out var value) && value is bool enabled)
            return enabled;
        var setting = await settings.GetAsync(SettingKey, context.RequestAborted);
        var result = setting.Value is true;
        context.Items[RequestItemKey] = result;
        return result;
    }
}
