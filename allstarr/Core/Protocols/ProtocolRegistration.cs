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
        services.AddSingleton(options);
        services.AddSingleton<ProtocolExecutionContextFactory>();
        services.AddSingleton<IProtocolLibraryScopeResolver, ProtocolLibraryScopeResolver>();
        return services;
    }
}
