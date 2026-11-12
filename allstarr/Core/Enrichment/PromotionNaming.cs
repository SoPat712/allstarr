using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace allstarr.Core.Enrichment;

public sealed record PromotionNamingResult(string? RelativePath, IReadOnlyList<PromotionNamingError> Errors);
public sealed record PromotionNamingError(int Position, int Length, string Message);

/// <summary>Evaluates the supported naming expressions without accessing the filesystem.</summary>
public static class PromotionNaming
{
    private const int MaxScriptLength = 16384;
    private const int MaxValueLength = 65536;
    private const int MaxDepth = 64;
    private const int MaxWork = 200000;
    private const int MaxSegmentBytes = 180;
    private const int MaxPathBytes = 1024;
    private const char Separator = '\uF000';
    private static readonly HashSet<string> Variables = new(StringComparer.Ordinal)
    {
        "artist", "albumartist", "album", "title", "date", "originaldate", "year", "originalyear",
        "tracknumber", "totaltracks", "discnumber", "totaldiscs", "genre", "label", "catalognumber",
        "releasetype", "releasecountry", "media", "_extension", "_quality"
    };

    private static readonly Dictionary<string, (int Minimum, int Maximum)> Functions = new(StringComparer.Ordinal)
    {
        ["if"] = (2, 3),
        ["if2"] = (1, 32),
        ["not"] = (1, 1),
        ["and"] = (1, 32),
        ["or"] = (1, 32),
        ["eq"] = (2, 2),
        ["ne"] = (2, 2),
        ["gt"] = (2, 2),
        ["lt"] = (2, 2),
        ["left"] = (2, 2),
        ["right"] = (2, 2),
        ["num"] = (2, 2),
        ["lower"] = (1, 1),
        ["upper"] = (1, 1),
        ["title"] = (1, 1),
        ["replace"] = (3, 3),
        ["rreplace"] = (3, 3),
        ["trim"] = (1, 2),
        ["noop"] = (0, 32),
        ["set"] = (2, 2),
        ["get"] = (1, 1),
        ["firstalphachar"] = (1, 2),
        ["initials"] = (1, 1)
    };

    internal static bool IsSupportedVariable(string name) => Variables.Contains(name) ||
        (name.StartsWith("musicbrainz_", StringComparison.Ordinal) && name.EndsWith("id", StringComparison.Ordinal) &&
         name.Length > 13 && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    public static IReadOnlyList<PromotionNamingError> Validate(string script)
    {
        try
        {
            _ = Parse(script);
            return [];
        }
        catch (NamingException error)
        {
            return [error.Error];
        }
    }

    public static PromotionNamingResult Render(string script, IReadOnlyDictionary<string, string?> fields,
        IEnumerable<string>? existingPaths = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        try
        {
            var nodes = Parse(script);
            var context = new EvaluationContext(fields);
            var value = context.Evaluate(nodes);
            var segments = value.Split(Separator);
            if (segments.Any(segment => segment is "." or "..") || value.StartsWith(Separator))
                throw Error(0, script.Length, "The expression produces an unsafe relative path.");
            if (segments.Length > 128)
                throw Error(0, script.Length, "The expression produces too many folders.");
            segments = segments.Select(SanitizeSegment).ToArray();
            var path = FitPath(segments, "");
            if (existingPaths is not null)
            {
                var fittedSegments = path.Split('/');
                var folder = fittedSegments.Length > 1 ? string.Join('/', fittedSegments[..^1]) + "/" : "";
                var filenameBudget = Math.Min(MaxSegmentBytes, MaxPathBytes - Encoding.UTF8.GetByteCount(folder));
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingCount = 0;
                foreach (var item in existingPaths)
                {
                    context.Charge(1);
                    if (++existingCount > 50000) throw Error(0, 0, "The supplied collision inventory is too large.");
                    if (item is { Length: <= MaxPathBytes })
                        existing.Add(NormalizeUnicode(item.Replace('\\', '/')));
                }
                for (var collision = 2; existing.Contains(path); collision++)
                {
                    context.Charge(1);
                    path = folder + FitSegment(fittedSegments[^1], filenameBudget,
                        $" ({collision.ToString(CultureInfo.InvariantCulture)})", true);
                }
            }
            return new(path, []);
        }
        catch (NamingException error)
        {
            return new(null, [error.Error]);
        }
    }

    /// <summary>Checks lexical containment only; callers separately control filesystem links.</summary>
    public static string ContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Contains('\\') || relativePath.Contains('\0') || relativePath.Contains(':') || relativePath.StartsWith('/'))
            throw new ArgumentException("A relative path using forward slashes is required.", nameof(relativePath));
        var parts = relativePath.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("The relative path contains an unsafe segment.", nameof(relativePath));
        var absoluteRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(absoluteRoot, Path.Combine(parts)));
        var prefix = Path.EndsInDirectorySeparator(absoluteRoot) ? absoluteRoot : absoluteRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            throw new ArgumentException("The relative path leaves the selected root.", nameof(relativePath));
        return candidate;
    }

    private static IReadOnlyList<Node> Parse(string script)
    {
        if (script is null)
            throw Error(0, 0, "A naming expression is required.");
        if (script.Length == 0 || script.Length > MaxScriptLength)
            throw Error(0, script.Length, $"The expression must contain 1 to {MaxScriptLength} characters.");
        var nodes = new Parser(script).Parse();
        ValidatePaths(nodes);
        ValidateRegularExpressions(nodes);
        return nodes;
    }

    private static void ValidatePaths(IReadOnlyList<Node> nodes)
    {
        var skeleton = string.Concat(nodes.Select(node => node is LiteralNode literal ? literal.Value : "x"));
        if (skeleton.StartsWith('/') || HasDrivePrefix(skeleton) || skeleton.Split('/').Any(part => part is "." or ".."))
        {
            var first = nodes.FirstOrDefault();
            throw Error(first?.Position ?? 0, first?.Length ?? 0, "Absolute paths and traversal segments are not supported.");
        }
        foreach (var call in nodes.OfType<CallNode>())
        {
            if (call.Name == "if")
                foreach (var branch in call.Arguments.Skip(1)) ValidatePaths(branch);
            else if (call.Name == "if2")
                foreach (var branch in call.Arguments) ValidatePaths(branch);
            else if (call.Name is "lower" or "upper" or "title" or "trim" or "left" or "right" or "replace" or "rreplace")
                ValidatePaths(call.Arguments[0]);
        }
    }

    private static void ValidateRegularExpressions(IReadOnlyList<Node> nodes)
    {
        var pending = new Stack<Node>(nodes);
        var count = 0;
        while (pending.TryPop(out var node))
        {
            if (node is not CallNode call) continue;
            foreach (var argument in call.Arguments)
                foreach (var child in argument) pending.Push(child);
            if (call.Name != "rreplace") continue;
            if (++count > 16) throw Error(call.Position, call.Length, "The expression contains too many regular expression operations.");
            if (!call.Arguments[1].All(child => child is LiteralNode)) continue;
            var pattern = string.Concat(call.Arguments[1].Cast<LiteralNode>().Select(child => child.Value));
            if (pattern.Length > 1024) throw Error(call.Position, call.Length, "The regular expression is too long.");
            try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { throw Error(call.Position, call.Length, "The regular expression is invalid."); }
        }
    }

    private static bool HasDrivePrefix(string value) => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder();
        foreach (var rune in NormalizeUnicode(value).EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.Value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || rune.Value == Separator)
                builder.Append('_');
            else builder.Append(rune.ToString());
        }
        var result = builder.ToString().TrimEnd(' ', '.');
        if (result.Length == 0) return "_";
        var stem = result.Split('.')[0].TrimEnd(' ', '.');
        if (IsReserved(stem)) result = "_" + result;
        return result;
    }

    private static bool IsReserved(string stem) => stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
        (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
         (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));

    private static string NormalizeUnicode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes()) builder.Append(rune.ToString());
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static string AddCollisionSuffix(string relativePath, int collision)
    {
        if (collision < 2) throw new ArgumentOutOfRangeException(nameof(collision));
        var segments = relativePath.Replace('\\', '/').Split('/');
        var folder = segments.Length > 1 ? string.Join('/', segments[..^1]) + "/" : "";
        var budget = Math.Min(MaxSegmentBytes, MaxPathBytes - Encoding.UTF8.GetByteCount(folder));
        return folder + FitSegment(segments[^1], budget, $" ({collision.ToString(CultureInfo.InvariantCulture)})", true);
    }

    private static string FitPath(string[] segments, string suffix)
    {
        var budgets = Enumerable.Repeat(MaxSegmentBytes, segments.Length).ToArray();
        var rendered = segments.Select((segment, index) => FitSegment(segment, budgets[index], index == segments.Length - 1 ? suffix : "", index == segments.Length - 1)).ToArray();
        var excess = Encoding.UTF8.GetByteCount(string.Join('/', rendered)) - MaxPathBytes;
        while (excess > 0)
        {
            var changed = false;
            for (var index = 0; index < rendered.Length && excess > 0; index++)
            {
                var currentBytes = Encoding.UTF8.GetByteCount(rendered[index]);
                var minimum = index == rendered.Length - 1 ? Math.Min(64, currentBytes) : 1;
                if (currentBytes <= minimum) continue;
                budgets[index] = currentBytes - Math.Min(excess, currentBytes - minimum);
                var next = FitSegment(segments[index], budgets[index], index == segments.Length - 1 ? suffix : "", index == segments.Length - 1);
                var saved = currentBytes - Encoding.UTF8.GetByteCount(next);
                rendered[index] = next;
                excess -= saved;
                changed |= saved > 0;
            }
            if (!changed) throw Error(0, 0, "The resulting path cannot fit within the path limit.");
        }
        return string.Join('/', rendered);
    }

    private static string FitSegment(string segment, int budget, string suffix, bool preserveExtension)
    {
        var extensionIndex = preserveExtension ? segment.LastIndexOf('.') : -1;
        var extension = extensionIndex > 0 ? segment[extensionIndex..] : "";
        if (Encoding.UTF8.GetByteCount(extension) > 40) extension = "";
        var stem = extension.Length > 0 ? segment[..^extension.Length] : segment;
        var remaining = budget - Encoding.UTF8.GetByteCount(suffix + extension);
        if (remaining < 1) throw Error(0, 0, "The filename suffix exceeds the path limit.");
        stem = TruncateUtf8(stem, remaining).TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "_";
        if (IsReserved(stem)) stem = "_" + TruncateUtf8(stem, Math.Max(0, remaining - 1));
        return stem + suffix + extension;
    }

    private static string TruncateUtf8(string value, int bytes)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > bytes) break;
            builder.Append(rune.ToString());
            bytes -= rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }

    private static NamingException Error(int position, int length, string message) => new(new(position, length, message));
    private sealed class NamingException(PromotionNamingError error) : Exception(error.Message)
    {
        public PromotionNamingError Error { get; } = error;
    }

    private abstract record Node(int Position, int Length);
    private sealed record LiteralNode(int Position, int Length, string Value) : Node(Position, Length);
    private sealed record FieldNode(int Position, int Length, string Name) : Node(Position, Length);
    private sealed record CallNode(int Position, int Length, string Name, IReadOnlyList<IReadOnlyList<Node>> Arguments) : Node(Position, Length);

    private sealed class Parser(string script)
    {
        private int position;
        private int nodeCount;

        public IReadOnlyList<Node> Parse() => ParseSequence(false, 0);

        private IReadOnlyList<Node> ParseSequence(bool argument, int depth)
        {
            if (depth > MaxDepth) throw Error(position, 0, "The expression nesting limit was exceeded.");
            var nodes = new List<Node>();
            while (position < script.Length)
            {
                var start = position;
                var character = script[position];
                if (argument && character is ',' or ')') break;
                Node node;
                if (character == '%')
                {
                    position++;
                    var end = script.IndexOf('%', position);
                    if (end < 0) throw Error(start, script.Length - start, "The variable is missing its closing percent sign.");
                    var name = script[position..end];
                    position = end + 1;
                    if (!IsSupportedVariable(name)) throw Error(start, position - start, "The variable is not supported.");
                    if (name == "_extension" && start > 0 && script[start - 1] == '.' && position < script.Length)
                        throw Error(position, script.Length - position, "Nothing may follow the file extension");
                    node = new FieldNode(start, position - start, name);
                }
                else if (character == '$') node = ParseCall(depth);
                else
                {
                    var literal = new StringBuilder();
                    while (position < script.Length && script[position] is not '%' and not '$')
                    {
                        character = script[position];
                        if (character is ',' or ')')
                        {
                            if (argument) break;
                            throw Error(position, 1, "This punctuation must be escaped with a backslash.");
                        }
                        if (character == '(') throw Error(position, 1, "A literal parenthesis must be escaped with a backslash.");
                        position++;
                        if (character == '\\')
                        {
                            if (position == script.Length) throw Error(position - 1, 1, "A trailing escape is incomplete.");
                            character = script[position++];
                        }
                        literal.Append(character);
                    }
                    node = new LiteralNode(start, position - start, literal.ToString());
                }
                if (++nodeCount > 4096) throw Error(start, position - start, "The expression contains too many parts.");
                nodes.Add(node);
            }
            return nodes;
        }

        private CallNode ParseCall(int depth)
        {
            var start = position++;
            var nameStart = position;
            while (position < script.Length && (char.IsAsciiLetterOrDigit(script[position]) || script[position] == '_')) position++;
            var name = script[nameStart..position];
            if (!Functions.TryGetValue(name, out var arity)) throw Error(start, position - start, "The function is not supported.");
            if (position == script.Length || script[position] != '(') throw Error(start, position - start, "A function requires parentheses.");
            position++;
            var arguments = new List<IReadOnlyList<Node>>();
            if (name == "noop" && position < script.Length && script[position] == ')') position++;
            else
            {
                while (true)
                {
                    arguments.Add(ParseSequence(true, depth + 1));
                    if (arguments.Count > 32) throw Error(start, position - start, "The function has too many arguments.");
                    if (position == script.Length) throw Error(start, position - start, "The function is missing its closing parenthesis.");
                    if (script[position++] == ')') break;
                }
            }
            if (arguments.Count < arity.Minimum || arguments.Count > arity.Maximum)
                throw Error(start, position - start, "The function has an unsupported number of arguments.");
            return new(start, position - start, name, arguments);
        }
    }

    private sealed class EvaluationContext
    {
        private readonly Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
        private int work;
        private int regexCount;

        public EvaluationContext(IReadOnlyDictionary<string, string?> input)
        {
            foreach (var pair in input)
            {
                Charge(1);
                if (pair.Value is null) continue;
                if (pair.Value.Length > MaxValueLength) throw Error(0, 0, "A metadata value exceeds the supported length.");
                Charge(pair.Value.Length);
                fields[pair.Key] = pair.Value.Replace(Separator, '_');
            }
        }

        public void Charge(int amount)
        {
            work += amount;
            if (work > MaxWork) throw Error(0, 0, "The naming expression exceeded its work limit.");
        }

        public string Evaluate(IReadOnlyList<Node> nodes)
        {
            var builder = new StringBuilder();
            foreach (var node in nodes)
            {
                Charge(1);
                var value = node switch
                {
                    LiteralNode literal => literal.Value.Replace(Separator, '_').Replace('/', Separator),
                    FieldNode field => variables.GetValueOrDefault(field.Name, fields.GetValueOrDefault(field.Name, "")),
                    CallNode call => EvaluateCall(call),
                    _ => ""
                };
                if (builder.Length + value.Length > MaxValueLength) throw Error(node.Position, node.Length, "The expression output exceeds its supported length.");
                Charge(value.Length);
                builder.Append(value);
            }
            return builder.ToString();
        }

        private string EvaluateCall(CallNode call)
        {
            string Argument(int index) => Evaluate(call.Arguments[index]);
            string Truth(bool value) => value ? "1" : "";
            try
            {
                switch (call.Name)
                {
                    case "if": return Argument(0).Length > 0 ? Argument(1) : call.Arguments.Count > 2 ? Argument(2) : "";
                    case "if2":
                        foreach (var argument in call.Arguments)
                        {
                            var value = Evaluate(argument);
                            if (value.Length > 0) return value;
                        }
                        return "";
                    case "and": return Truth(call.Arguments.All(argument => Evaluate(argument).Length > 0));
                    case "or": return Truth(call.Arguments.Any(argument => Evaluate(argument).Length > 0));
                    case "not": return Truth(Argument(0).Length == 0);
                    case "noop": return "";
                    case "set":
                        var name = Argument(0);
                        if (name.Length > 128) throw Error(call.Position, call.Length, "The temporary variable name is too long.");
                        variables[name] = Argument(1);
                        return "";
                    case "get":
                        var key = Argument(0);
                        return variables.GetValueOrDefault(key, fields.GetValueOrDefault(key, ""));
                }
                var arguments = call.Arguments.Select(Evaluate).ToArray();
                var first = arguments[0];
                switch (call.Name)
                {
                    case "eq": return Truth(first.Replace(Separator, '/').Equals(arguments[1].Replace(Separator, '/'), StringComparison.Ordinal));
                    case "ne": return Truth(!first.Replace(Separator, '/').Equals(arguments[1].Replace(Separator, '/'), StringComparison.Ordinal));
                    case "gt": return Truth(Number(first) > Number(arguments[1]));
                    case "lt": return Truth(Number(first) < Number(arguments[1]));
                    case "lower": return first.ToLowerInvariant();
                    case "upper": return first.ToUpperInvariant();
                    case "title": return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(first.ToLowerInvariant());
                    case "left": return string.Concat(first.EnumerateRunes().Take(Count(arguments[1])).Select(rune => rune.ToString()));
                    case "right":
                        var runes = first.EnumerateRunes().ToArray();
                        return string.Concat(runes.Skip(Math.Max(0, runes.Length - Count(arguments[1]))).Select(rune => rune.ToString()));
                    case "num":
                        var width = Count(arguments[1]);
                        if (width > 128) throw Error(call.Position, call.Length, "Numeric padding is limited to 128 characters.");
                        var number = decimal.Truncate(Number(first));
                        return number < 0 ? "-" + Math.Abs(number).ToString("0", CultureInfo.InvariantCulture).PadLeft(Math.Max(0, width - 1), '0') :
                            number.ToString("0", CultureInfo.InvariantCulture).PadLeft(width, '0');
                    case "trim":
                        if (arguments.Length == 1) return first.Trim();
                        var trimCharacters = arguments[1].Replace(Separator, '/');
                        if (trimCharacters.Contains('/')) trimCharacters += Separator;
                        return first.Trim(trimCharacters.ToCharArray());
                    case "replace":
                        if (arguments[1].Length == 0) return first;
                        var comparable = first.Replace(Separator, '/');
                        var search = arguments[1].Replace(Separator, '/');
                        var occurrences = CountOccurrences(comparable, search);
                        if ((long)first.Length + (long)occurrences * (arguments[2].Length - arguments[1].Length) > MaxValueLength)
                            throw Error(call.Position, call.Length, "The replacement output exceeds the supported length.");
                        var replaced = new StringBuilder();
                        var start = 0;
                        int found;
                        while ((found = comparable.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
                        {
                            replaced.Append(first, start, found - start).Append(arguments[2]);
                            start = found + search.Length;
                        }
                        return replaced.Append(first, start, first.Length - start).ToString();
                    case "rreplace":
                        if (++regexCount > 16) throw Error(call.Position, call.Length, "The expression contains too many regular expression operations.");
                        if (arguments[1].Length > 1024) throw Error(call.Position, call.Length, "The regular expression is too long.");
                        var regex = new Regex(arguments[1].Replace(Separator, '/'), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                        var output = new StringBuilder();
                        var cursor = 0;
                        foreach (Match match in regex.Matches(first.Replace(Separator, '/')))
                        {
                            Charge(25 + arguments[2].Length);
                            output.Append(first, cursor, match.Index - cursor);
                            AppendReplacement(output, match, first, arguments[2], call);
                            cursor = match.Index + match.Length;
                        }
                        if (output.Length + first.Length - cursor > MaxValueLength) throw Error(call.Position, call.Length, "The replacement output exceeds the supported length.");
                        output.Append(first, cursor, first.Length - cursor);
                        return output.ToString();
                    case "firstalphachar":
                        var initial = first.EnumerateRunes().FirstOrDefault();
                        return Rune.IsLetter(initial) ? Rune.ToUpperInvariant(initial).ToString() : arguments.Length > 1 ? arguments[1] : "#";
                    case "initials":
                        var initials = new StringBuilder();
                        var inWord = false;
                        foreach (var rune in first.EnumerateRunes())
                        {
                            if (Rune.IsLetterOrDigit(rune))
                            {
                                if (!inWord) initials.Append(Rune.ToUpperInvariant(rune).ToString());
                                inWord = true;
                            }
                            else inWord = false;
                        }
                        return initials.ToString();
                }
                throw Error(call.Position, call.Length, "The function is not supported.");
            }
            catch (RegexMatchTimeoutException)
            {
                throw Error(call.Position, call.Length, "The regular expression exceeded its time limit.");
            }
            catch (ArgumentException)
            {
                throw Error(call.Position, call.Length, "The function contains an invalid argument or regular expression.");
            }
        }

        private static decimal Number(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0;
        private static int Count(string value) => (int)Math.Clamp(decimal.Truncate(Number(value)), 0, MaxValueLength);

        private static void AppendReplacement(StringBuilder output, Match match, string original, string replacement, CallNode call)
        {
            for (var index = 0; index < replacement.Length; index++)
            {
                string value;
                if (replacement[index] == '\\' && index + 1 < replacement.Length)
                {
                    var start = ++index;
                    if (char.IsAsciiDigit(replacement[index]))
                    {
                        while (index + 1 < replacement.Length && char.IsAsciiDigit(replacement[index + 1])) index++;
                        var group = match.Groups[replacement[start..(index + 1)]];
                        value = original.Substring(group.Index, group.Length);
                    }
                    else if (replacement[index] == 'g' && index + 1 < replacement.Length && replacement[index + 1] == '<')
                    {
                        var end = replacement.IndexOf('>', index + 2);
                        if (end < 0) throw Error(call.Position, call.Length, "The regular expression replacement has an incomplete group reference.");
                        var group = match.Groups[replacement[(index + 2)..end]];
                        value = original.Substring(group.Index, group.Length);
                        index = end;
                    }
                    else value = replacement[index] switch { 'n' => "\n", 'r' => "\r", 't' => "\t", _ => replacement[index].ToString() };
                }
                else value = replacement[index].ToString();
                if (output.Length + value.Length > MaxValueLength) throw Error(call.Position, call.Length, "The replacement output exceeds the supported length.");
                output.Append(value);
            }
        }
        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var start = 0;
            while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
            {
                count++;
                start += value.Length;
            }
            return count;
        }
    }
}
