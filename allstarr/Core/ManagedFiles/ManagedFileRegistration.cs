namespace allstarr.Core.ManagedFiles;

public static class ManagedFileRegistration
{
    public static IServiceCollection AddManagedFilePlacement(this IServiceCollection services)
    {
        services.AddScoped<EfManagedFileOwnershipStore>();
        services.AddScoped<IManagedFileOwnershipStore>(provider => provider.GetRequiredService<EfManagedFileOwnershipStore>());
        services.AddScoped<IManagedFileRemovalStore>(provider => provider.GetRequiredService<EfManagedFileOwnershipStore>());
        services.AddSingleton<IManagedFileOperations, PhysicalManagedFileOperations>();
        services.AddScoped<FilePlacementService>();
        services.AddScoped<ManagedFileRemovalService>();
        return services;
    }
}
