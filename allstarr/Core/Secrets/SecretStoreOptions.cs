namespace allstarr.Core.Secrets;

public sealed class SecretStoreOptions
{
    public const string SectionName = "Secrets";

    public string KeyRingPath { get; set; } = "/run/secrets/allstarr-keyring.json";

    public int MaxSecretBytes { get; set; } = 64 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyRingPath))
        {
            throw new InvalidOperationException("Secrets:KeyRingPath is required.");
        }

        if (MaxSecretBytes is < 1 or > 1024 * 1024)
        {
            throw new InvalidOperationException("Secrets:MaxSecretBytes must be between 1 and 1048576.");
        }
    }
}
