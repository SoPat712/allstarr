namespace allstarr.Models.Admin;

public class ConfigUpdateRequest
{
    public Dictionary<string, string> Updates { get; set; } = new();
}
