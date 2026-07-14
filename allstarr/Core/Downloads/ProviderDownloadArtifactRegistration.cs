namespace allstarr.Core.Downloads;

public static class ProviderDownloadArtifactRegistration
{
    public static IServiceCollection AddProviderDownloadArtifacts(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ProviderDownloadWorkspaceOptions.SectionName).Get<ProviderDownloadWorkspaceOptions>() ?? new();
        services.AddSingleton(options);
        services.AddSingleton<IProviderDownloadArtifactStore, EfProviderDownloadArtifactStore>();
        services.AddSingleton<ProviderDownloadArtifactResolver>();
        return services;
    }
}
