using System.Diagnostics;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Music;

internal sealed class MixGenerationDiagnostics
{
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Dictionary<string, int> stages = [];
    private readonly Dictionary<string, int> failures = [];
    private readonly Dictionary<string, long> phases = [];
    internal readonly List<InboxCounts> Inbox = [];
    internal InboxCounts? CurrentEntry;
    internal int Reads;

    internal sealed class InboxCounts(string[] keys)
    {
        internal readonly string[] Keys = keys;
        internal readonly Dictionary<string, int> Counts = [];
        internal void Count(string reason, int count = 1) => Counts[reason] = Counts.GetValueOrDefault(reason) + count;
    }

    internal void Count(string stage, int count = 1) => stages[stage] = stages.GetValueOrDefault(stage) + count;
    internal void Failure(string provider, string kind)
    {
        var safeProvider = provider.Length <= 80 && provider.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? provider : "unknown";
        var key = safeProvider + "/" + kind;
        failures[key] = failures.GetValueOrDefault(key) + 1;
    }

    internal static string ErrorKind(Exception error) => error switch
    {
        UnauthorizedAccessException => nameof(ProviderErrorKind.Unauthorized),
        FileNotFoundException => nameof(ProviderErrorKind.NotFound),
        NotSupportedException => nameof(ProviderErrorKind.CapabilityUnavailable),
        OperationCanceledException => "Timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => nameof(ProviderErrorKind.RateLimited),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => nameof(ProviderErrorKind.Unauthorized),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => nameof(ProviderErrorKind.Forbidden),
        HttpRequestException => nameof(ProviderErrorKind.TransientFailure),
        _ => "Other"
    };

    internal IDisposable Phase(string name) => new Timing(elapsed => phases[name] = phases.GetValueOrDefault(name) + elapsed);
    private sealed class Timing(Action<long> complete) : IDisposable
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        public void Dispose() => complete(elapsed.ElapsedMilliseconds);
    }

    private static string Counts<T>(IEnumerable<KeyValuePair<string, T>> values) => string.Join(",", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
    internal void Log(ILogger<PersonalMixService>? logger, string kind, MixRecipeEvidence? evidence, bool completed)
    {
        if (logger == null) return;
        var inbox = kind == "release-radar" ? string.Join(";", Inbox.Select((entry, index) =>
            "entry" + (index + 1) + "[" + Counts(entry.Counts) + "]")) : "";
        logger.LogInformation("Mix generation {Kind}: shared stages [{Stages}]; live={Live}, imported={Imported}, primary={Primary}, fallback={Fallback}, candidates={Candidates}, qualified={Qualified}, shown={Shown}, music={Music}, services={Services}; excluded [{Excluded}]; inbox [{Inbox}]; provider failures [{Failures}]; reads={Reads}; shared phases ms [{Phases},total={Total}]; completed={Completed}",
            kind, Counts(stages), evidence?.LivePlays ?? 0, evidence?.ImportedPlays ?? 0, evidence?.PrimaryPlays ?? 0, evidence?.FallbackPlays ?? 0,
            evidence?.Candidates ?? 0, evidence?.Qualified ?? 0, evidence?.Shown ?? 0, evidence?.FromMusic ?? 0, evidence?.FromServices ?? 0,
            Counts(evidence?.Excluded ?? new Dictionary<string, int>()), inbox, Counts(failures), evidence?.ProviderReads ?? Reads,
            Counts(phases), total.ElapsedMilliseconds, completed ? 1 : 0);
    }
}
