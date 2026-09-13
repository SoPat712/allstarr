namespace allstarr.Core.Downloads;

public static class ProviderDownloadArtifactRegistration
{
    public static IServiceCollection AddProviderDownloadArtifacts(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ProviderDownloadWorkspaceOptions.SectionName).Get<ProviderDownloadWorkspaceOptions>() ?? new();
        services.AddSingleton(options);
        services.AddSingleton<IProviderDownloadArtifactStore, EfProviderDownloadArtifactStore>();
        services.AddSingleton<ProviderDownloadArtifactResolver>();
        services.AddSingleton<ManagedTrackDownloadService>();
        var placement = new ManagedTrackPlacementOptions();
        configuration.GetSection("FavoriteActions:Placement").Bind(placement);
        if (placement.RootId == Guid.Empty) placement.RootId = ManagedTrackPlacementOptions.DefaultRootId;
        if (string.IsNullOrWhiteSpace(placement.RootPath))
        {
            var downloadRoot = configuration["Library:DownloadPath"] ?? "./downloads";
            placement.RootPath = configuration["Library:KeptPath"] ?? Path.Combine(downloadRoot, "kept");
        }
        services.AddSingleton(placement);
        return services;
    }
}
