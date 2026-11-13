using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientApiV2;

internal static class V2ProviderUrls
{
    internal static ItemDetailsProvider For(ItemDetailsProvider provider)
    {
        var url = $"/allstarr/v2/providers/{Uri.EscapeDataString(provider.Id)}/icon";
        return provider with { IconUrl = provider.IconUrl == null ? null : url, GlyphUrl = provider.GlyphUrl == null ? null : url + "?variant=glyph" };
    }

    internal static ClientItemMatch? For(ClientItemMatch? match) => match == null ? null : match with
    {
        Current = match.Current == null ? null : match.Current with { Provider = For(match.Current.Provider) },
        Candidates = match.Candidates.Select(candidate => candidate with { Provider = For(candidate.Provider) }).ToArray()
    };
}
