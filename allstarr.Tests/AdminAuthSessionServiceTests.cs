using allstarr.Services.Admin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class AdminAuthSessionServiceTests
{
    [Fact]
    public void SubsonicIdentitySession_PersistsWithoutPersistingBackendPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")),
                options => options.SetApplicationName("allstarr-admin-session-test"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admin:SessionStorePath"] = Path.Combine(root, "sessions.protected")
                })
                .Build();
            var first = new AdminAuthSessionService(
                dataProtection,
                NullLogger<AdminAuthSessionService>.Instance,
                configuration);
            var tenantId = Guid.CreateVersion7();
            var allstarrUserId = Guid.CreateVersion7();
            var created = first.CreateSession(
                userId: "alice",
                userName: "alice",
                isAdministrator: true,
                jellyfinAccessToken: string.Empty,
                jellyfinServerId: null,
                backendType: "Subsonic",
                tenantId: tenantId,
                allstarrUserId: allstarrUserId);

            var restoredService = new AdminAuthSessionService(
                dataProtection,
                NullLogger<AdminAuthSessionService>.Instance,
                configuration);

            Assert.True(restoredService.TryGetValidSession(created.SessionId, out var restored));
            Assert.Equal("Subsonic", restored.BackendType);
            Assert.Equal(string.Empty, restored.JellyfinAccessToken);
            Assert.Equal(tenantId, restored.TenantId);
            Assert.Equal(allstarrUserId, restored.AllstarrUserId);

            var protectedPayload = File.ReadAllText(configuration["Admin:SessionStorePath"]!);
            Assert.DoesNotContain("alice", protectedPayload, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptSessionStore_LogsOnlyExceptionType()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storePath = Path.Combine(root, "sessions-private-name.protected");
            File.WriteAllText(storePath, "not-a-valid-protected-payload-private-token");
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")),
                options => options.SetApplicationName("allstarr-admin-session-redaction-test"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admin:SessionStorePath"] = storePath
                })
                .Build();
            var entries = new List<(string Message, Exception? Exception)>();

            _ = new AdminAuthSessionService(
                dataProtection,
                new CollectingLogger<AdminAuthSessionService>(entries),
                configuration);

            var entry = Assert.Single(entries);
            Assert.Null(entry.Exception);
            Assert.Contains("CryptographicException", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-token", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(storePath, entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class CollectingLogger<T>(List<(string Message, Exception? Exception)> entries)
        : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add((formatter(state, exception), exception));
    }
}
