namespace allstarr.Core.Identity;

public sealed class ProviderAccountOptions
{
    public const string SectionName = "ProviderAccounts";
    public const string ListenerConnectionsKey = "ProviderAccounts:ListenersCanConnectOwnAccounts";

    public bool ListenersCanConnectOwnAccounts { get; set; } = true;
}
