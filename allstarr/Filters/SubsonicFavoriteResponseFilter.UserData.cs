using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Music;

namespace allstarr.Filters;

public sealed partial class SubsonicFavoriteResponseFilter
{
    private async Task HydrateJsonUserDataAsync(JsonObject[] items, HttpContext http)
    {
        if (userData == null) return;
        var saved = await userData.ReadForRequestAsync(http, items.Select(item => item["id"]!.GetValue<string>()));
        foreach (var item in items)
            if (ProviderUserDataStore.IsProviderItem(item["id"]!.GetValue<string>()))
                MusicUserDataProjection.Subsonic(item, saved.GetValueOrDefault(item["id"]!.GetValue<string>()) ?? new());
    }

    private async Task HydrateXmlUserDataAsync(XElement[] items, HttpContext http)
    {
        if (userData == null) return;
        var saved = await userData.ReadForRequestAsync(http, items.Select(item => item.Attribute("id")!.Value));
        foreach (var item in items)
            if (ProviderUserDataStore.IsProviderItem(item.Attribute("id")!.Value))
                MusicUserDataProjection.Subsonic(item, saved.GetValueOrDefault(item.Attribute("id")!.Value) ?? new());
    }
}
