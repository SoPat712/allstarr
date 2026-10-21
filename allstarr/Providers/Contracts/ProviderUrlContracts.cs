namespace allstarr.Core.Capabilities;

public interface IProviderUrlCapability : IProviderCapability
{
    IReadOnlyList<string> UrlPatterns { get; }

    Task<ProviderOutcome<ProviderExternalResourceId>> ResolveUrlAsync(
        ProviderExecutionContext context,
        string url);
}
