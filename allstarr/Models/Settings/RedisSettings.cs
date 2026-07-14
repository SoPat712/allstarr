namespace allstarr.Models.Settings;

public class RedisSettings
{
    public bool Enabled { get; set; } = true;
    public string ConnectionString { get; set; } = "localhost:6379";
    public int ReconnectIntervalSeconds { get; set; } = 30;
}
