using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Core.ClientApiV2;

public sealed class V2PlaybackTokens(IDataProtectionProvider protection)
{
    public const string Purpose = "allstarr.client.v2.stream.v1";
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    internal string Protect(V2PlaybackTicket ticket, DateTimeOffset expires) => protector.Protect(JsonSerializer.Serialize(ticket), expires);

    internal V2PlaybackTicket? Read(string token)
    {
        if (token.Length is 0 or > 16384) return null;
        try { return JsonSerializer.Deserialize<V2PlaybackTicket>(protector.Unprotect(token, out _)); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or ArgumentException) { return null; }
    }
}

internal sealed record V2PlaybackTicket(Guid Listener, string Backend, string Principal, string Item, string Tier, string PlaybackId,
    ProviderAudioQuality Quality, ProviderAudioDeliveryRequest Delivery, ProviderMediaFormat Source)
{
    public string? DeviceId { get; init; }
    public string? TrackId { get; init; }
}
