namespace allstarr.Services.Common;

public static class EndpointUsagePathResolver
{
    public const string DefaultDirectory = "/app/cache/endpoint-usage";

    public static string GetDirectory(IConfiguration configuration)
    {
        var configured = configuration["Diagnostics:EndpointUsageDirectory"];
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultDirectory
            : Path.GetFullPath(configured);
    }

    public static string GetLogFile(IConfiguration configuration) =>
        Path.Combine(GetDirectory(configuration), "endpoints.csv");
}
