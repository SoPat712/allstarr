using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.ClientApi;

public sealed class LibraryProviderResolver(LibraryBackendBrandResolver backend)
{
    public async Task<ItemDetailsProvider> ResolveAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var brand = await backend.ResolveAsync(execution, execution.CancellationToken);
        var icons = await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [brand.Id], execution.CancellationToken);
        return icons.GetValueOrDefault(brand.Id)?.Provider(brand.Id, brand.Name) ?? new(brand.Id, brand.Name, null);
    }
}
