using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace allstarr.Core.Music;

internal static class MusicUserDataProjection
{
    public static void Jellyfin(JsonObject target, ProviderItemUserData data)
    {
        target["PlaybackPositionTicks"] = data.PlaybackPositionTicks;
        target["PlayCount"] = data.PlayCount;
        target["Played"] = data.Played;
        if (data.LastPlayedDate is { } played) target["LastPlayedDate"] = Timestamp(played);
        else target.Remove("LastPlayedDate");
        if (data.Rating > 0) target["Rating"] = data.Rating;
        else target.Remove("Rating");
    }

    public static void Subsonic(JsonObject target, ProviderItemUserData data)
    {
        target["userRating"] = data.Rating;
        target["playCount"] = data.PlayCount;
        if (data.LastPlayedDate is { } played) target["played"] = Timestamp(played);
        else target.Remove("played");
    }

    public static void Subsonic(XElement target, ProviderItemUserData data)
    {
        target.SetAttributeValue("userRating", data.Rating);
        target.SetAttributeValue("playCount", data.PlayCount);
        target.SetAttributeValue("played", data.LastPlayedDate is { } played ? Timestamp(played) : null);
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
