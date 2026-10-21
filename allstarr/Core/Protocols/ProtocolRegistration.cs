namespace allstarr.Core.Protocols;

public static class ProtocolRegistration
{
    public static IServiceCollection AddProtocolExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ProtocolExecutionOptions();
        configuration.GetSection(ProtocolExecutionOptions.SectionName).Bind(options);
        _ = options.GetOperationTimeout();
        _ = options.GetDownloadStreamTimeout();
        _ = options.GetDownloadDiagnosticTimeout();
        services.AddSingleton(options);
        services.AddSingleton<allstarr.Core.Playlists.WritablePlaylistMembership>();
        services.AddSingleton<ProtocolExecutionContextFactory>();
        services.AddHttpClient(BackendLibraryAccessResolver.HttpClientName);
        services.AddSingleton<IBackendLibraryPermissionSource, BackendLibraryPermissionSource>();
        services.AddSingleton<IBackendLibraryAccessResolver, BackendLibraryAccessResolver>();
        return services;
    }
}
