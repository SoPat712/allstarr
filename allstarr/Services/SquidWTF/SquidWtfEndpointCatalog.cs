namespace allstarr.Services.SquidWTF;

/// <summary>
/// Holds the discovered SquidWTF endpoint pools.
/// API endpoints are used for metadata/search calls.
/// Streaming endpoints are used for /track/ and audio streaming calls.
/// </summary>
public sealed class SquidWtfEndpointCatalog
{
    public SquidWtfEndpointCatalog(List<string> apiUrls, List<string> streamingUrls)
    {
        if (apiUrls == null)
        {
            throw new ArgumentNullException(nameof(apiUrls));
        }

        if (streamingUrls == null)
        {
            throw new ArgumentNullException(nameof(streamingUrls));
        }

        ApiUrls = apiUrls;
        StreamingUrls = streamingUrls;
        LoadedAtUtc = DateTime.UtcNow;
    }

    public List<string> ApiUrls { get; }
    public List<string> StreamingUrls { get; }
    public DateTime LoadedAtUtc { get; }
}
