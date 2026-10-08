namespace allstarr.Core.Jobs;

public static class DurableJobRegistration
{
    public static IServiceCollection AddDurableJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(DurableJobOptions.SectionName)
                          .Get<DurableJobOptions>()
                      ?? new DurableJobOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<JobPayloadPolicy>();
        services.AddSingleton<DurableJobContextAuthorizer>();
        services.AddSingleton<DurableJobQueue>();
        services.AddSingleton<DurableScheduleEngine>();
        services.AddSingleton<SidecarJobGate>();
        services.AddHostedService<DurableJobWorker>();
        services.AddHostedService<DurableScheduleWorker>();
        return services;
    }
}
