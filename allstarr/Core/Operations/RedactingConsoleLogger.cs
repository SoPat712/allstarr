using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Operations;

public sealed class RedactingConsoleLoggerProvider : ILoggerProvider
{
    private readonly IReadOnlyDictionary<string, LogLevel> _levels;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public RedactingConsoleLoggerProvider(
        IConfiguration configuration,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        _levels = configuration.GetSection("Logging:LogLevel")
            .GetChildren()
            .Select(item => new
            {
                item.Key,
                Parsed = Enum.TryParse<LogLevel>(item.Value, true, out var parsed)
                    ? parsed
                    : LogLevel.Information
            })
            .ToDictionary(item => item.Key, item => item.Parsed, StringComparer.OrdinalIgnoreCase);
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingConsoleLogger(categoryName, MinimumLevel(categoryName), _output, _error);

    public void Dispose()
    {
    }

    private LogLevel MinimumLevel(string category)
    {
        var match = _levels
            .Where(item => !item.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                           category.StartsWith(item.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Key.Length)
            .Select(item => (LogLevel?)item.Value)
            .FirstOrDefault();
        return match ?? (_levels.TryGetValue("Default", out var fallback)
            ? fallback
            : LogLevel.Information);
    }
}

internal sealed partial class RedactingConsoleLogger(
    string category,
    LogLevel minimumLevel,
    TextWriter output,
    TextWriter error) : ILogger
{
    private static readonly object WriteGate = new();
    private static readonly AsyncLocal<LogScope?> CurrentScope = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var scope = new LogScope(state, CurrentScope.Value);
        CurrentScope.Value = scope;
        return new ScopeLease(scope);
    }

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= minimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        string? template = null;
        AddScopeFields(fields);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var (key, value) in values)
            {
                if (key == "{OriginalFormat}")
                {
                    template = SafeOperationalText.Sanitize(value?.ToString(), 2000);
                    continue;
                }

                fields[key] = SensitiveFieldName().IsMatch(key)
                    ? "<redacted>"
                    : SafeValue(value);
            }
        }
        else
        {
            template = $"Unstructured log state ({state?.GetType().Name ?? "unknown"})";
        }

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = logLevel.ToString().ToLowerInvariant(),
            ["category"] = category,
            ["eventId"] = eventId.Id,
            ["messageTemplate"] = template,
            ["fields"] = fields.Count == 0 ? null : fields,
            ["exceptionType"] = exception?.GetType().Name
        };
        var writer = logLevel >= LogLevel.Warning ? error : output;
        lock (WriteGate)
        {
            writer.WriteLine(JsonSerializer.Serialize(payload));
            writer.Flush();
        }
    }

    private static object? SafeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal => value,
            Guid guid => guid.ToString("N"),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O"),
            _ => SafeOperationalText.Sanitize(value.ToString(), 500)
        };
    }

    private static void AddScopeFields(IDictionary<string, object?> fields)
    {
        var scopes = new Stack<LogScope>();
        for (var scope = CurrentScope.Value; scope != null; scope = scope.Parent)
        {
            scopes.Push(scope);
        }

        while (scopes.TryPop(out var scope))
        {
            if (scope.State is not IEnumerable<KeyValuePair<string, object>> values)
            {
                continue;
            }

            foreach (var (key, value) in values)
            {
                fields[key] = SensitiveFieldName().IsMatch(key)
                    ? "<redacted>"
                    : SafeValue(value);
            }
        }
    }

    private sealed record LogScope(object State, LogScope? Parent);

    private sealed class ScopeLease(LogScope scope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(CurrentScope.Value, scope))
            {
                CurrentScope.Value = scope.Parent;
            }
        }
    }

    [GeneratedRegex(
        "(^key$)|(^error$)|(^message$)|([.]message$)|token|password|secret|cookie|authorization|credential|api.?key|client.?id|private.?key|cachekey|connectionstring|dsn|arl|body|xml|json|header|commandtext|parameters|sessionid|playsessionid|response|content|payload|exception|preview|reasonphrase",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveFieldName();
}
