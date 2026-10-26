using System.Text.Json;
using allstarr.Services.Jellyfin;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private readonly Dictionary<string, string> _nativeQueryRows = new(JellyfinItemIdComparer.Instance);

    private void CaptureNativeQueryRows(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        if (item.TryGetProperty("Id", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } id && !JellyfinItemIdComparer.IsExternalId(id))
        {
            _nativeQueryRows.TryAdd(id, item.GetRawText());
            HttpContext.Items[JellyfinQueryResponseRows.NativeRowsKey] = _nativeQueryRows;
            return;
        }
        if (item.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var child in items.EnumerateArray()) CaptureNativeQueryRows(child);
    }

    private string PreserveNativeQueryRows(string body) => JellyfinQueryResponseRows.Preserve(body, HttpContext);
}
