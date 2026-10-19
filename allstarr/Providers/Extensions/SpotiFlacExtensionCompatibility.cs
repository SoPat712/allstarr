using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using Acornima;
using Acornima.Ast;

namespace allstarr.Core.Extensions;

public static class SpotiFlacExtensionCompatibility
{
    public const string Marker = "spotiflac-v1";
    public const string AppVersion = "5.1.1";

    // Adapted from SpotiFLAC-Mobile (MIT), extensions/src/environment.rs network_timeout.
    internal static TimeSpan NetworkTimeout(JsonElement manifest)
    {
        long seconds = 0;
        if (manifest.TryGetProperty("spotiflacManifest", out var source) &&
            source.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("networkTimeoutSeconds", out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
                long.TryParse(value.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                seconds = (long)Math.Clamp(number, 0, 300);
        }
        return TimeSpan.FromSeconds(seconds <= 0 ? 30 : Math.Clamp(seconds, 5, 300));
    }

    public static bool IsManifest(string json)
    {

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
               root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array &&
               !root.TryGetProperty("sdkVersion", out _);
    }

    public static bool IsNormalizedManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("compatibility", out var value) &&
               value.ValueKind == JsonValueKind.String && value.GetString() == Marker;
    }

    public static string EnsureDownloadStreamingCapability(string normalizedJson)
    {
        var root = JsonNode.Parse(normalizedJson)?.AsObject() ??
                   throw new ExtensionSdkValidationException("Extension manifest must be a JSON object.");
        var capabilities = root["capabilities"]?.AsArray() ?? [];
        if (capabilities.OfType<JsonObject>().Any(item =>
                Text(item, "kind").Equals("Streaming", StringComparison.OrdinalIgnoreCase)))
            return normalizedJson;
        var download = capabilities.OfType<JsonObject>().SingleOrDefault(item =>
            Text(item, "kind").Equals("Download", StringComparison.OrdinalIgnoreCase));
        if (download == null) return normalizedJson;
        capabilities.Add(new JsonObject
        {
            ["kind"] = "Streaming",
            ["hooks"] = new JsonArray("getStreamLease", "probeStream"),
            ["accountScopes"] = download["accountScopes"]?.DeepClone() ?? new JsonArray(),
            ["accountRequired"] = download["accountRequired"]?.DeepClone() ?? false
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool UsesDownloadBackedStreaming(ExtensionSdkManifest manifest) =>
        manifest.Compatibility == Marker ||
        manifest.Capabilities.Any(item => item.Kind == ProviderCapabilityKind.Download) &&
        manifest.Capabilities.All(item => item.Kind != ProviderCapabilityKind.Streaming);

    public static string NormalizeManifest(string originalJson, string indexJs)
    {
        var original = JsonNode.Parse(originalJson)?.AsObject() ??
                       throw new ExtensionSdkValidationException("SpotiFLAC manifest must be a JSON object.");
        var id = Text(original, "name").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id))
            throw new ExtensionSdkValidationException("SpotiFLAC manifest requires a name.");
        var extensionId = $"spotiflac-{id}";
        var types = original["type"]?.AsArray().Select(item => item?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal) ?? [];
        // Action buttons are commands in the source app, not persisted account settings.
        var settings = new JsonArray((original["settings"]?.AsArray() ?? [])
            .Where(item => item is not JsonObject setting ||
                           !Text(setting, "type").Trim().Equals("button", StringComparison.OrdinalIgnoreCase))
            .Select(item => item?.DeepClone()).ToArray());
        var hasSettings = settings.Count > 0;
        var requiresSettings = settings.OfType<JsonObject>().Any(item => item["required"]?.GetValue<bool>() == true);
        var capabilities = new JsonArray();

        if (types.Contains("metadata_provider"))
        {
            capabilities.Add(Capability("Metadata", hasSettings, requiresSettings,
                "searchTracks", "getTrack", "searchAlbums", "getAlbum", "searchArtists", "getArtist"));
        }
        if (types.Contains("lyrics_provider"))
            capabilities.Add(Capability("Lyrics", hasSettings, requiresSettings, "fetchLyrics"));
        if (types.Contains("download_provider") &&
            indexJs.Contains("download", StringComparison.Ordinal))
        {
            capabilities.Add(Capability("Download", hasSettings, requiresSettings, "checkAvailability", "download"));
            capabilities.Add(Capability("Streaming", hasSettings, requiresSettings, "getStreamLease", "probeStream"));
        }

        if (capabilities.Count == 0)
            throw new ExtensionSdkValidationException("SpotiFLAC extension does not expose a capability Allstarr can run yet.");

        var permissions = new JsonArray();
        if (original["permissions"] is JsonObject permissionObject)
        {
            if (permissionObject["network"] is JsonArray network)
            {
                foreach (var item in network)
                {
                    var host = item?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
                    if (host.Length == 0 || host is "localhost" or "127.0.0.1") continue;
                    if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
                    if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        host = new Uri(host).Host;
                    permissions.Add(Permission("Network", $"https://{host}/"));
                }
            }
            if (permissionObject["storage"]?.GetValue<bool>() == true)
                permissions.Add(Permission("Cache", "*"));
        }
        foreach (var setting in settings.OfType<JsonObject>())
        {
            var key = Text(setting, "key").Trim();
            var type = Text(setting, "type").Trim();
            var explicitlySecret = setting["secret"]?.GetValue<bool>() == true;
            var inferredSecret = key.EndsWith("token", StringComparison.OrdinalIgnoreCase) ||
                                 key.EndsWith("password", StringComparison.OrdinalIgnoreCase) ||
                                 key.EndsWith("secret", StringComparison.OrdinalIgnoreCase) ||
                                 key.EndsWith("cookie", StringComparison.OrdinalIgnoreCase) ||
                                 type.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                                 type.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                                 type.Equals("token", StringComparison.OrdinalIgnoreCase);
            if (key.Length > 0 && (explicitlySecret || inferredSecret))
                permissions.Add(Permission("Secret", key));
        }

        var normalized = new JsonObject
        {
            ["id"] = extensionId,
            ["displayName"] = Text(original, "displayName", id),
            ["version"] = Text(original, "version", "1.0.0"),
            ["sdkVersion"] = ExtensionSdkV1.Version,
            ["entryPoint"] = "index.js",
            ["capabilities"] = capabilities,
            ["permissions"] = permissions,
            ["description"] = original["description"]?.DeepClone(),
            ["author"] = original["author"]?.DeepClone(),
            ["icon"] = original["icon"]?.DeepClone(),
            ["settings"] = settings,
            ["qualityOptions"] = original["qualityOptions"]?.DeepClone() ?? new JsonArray(),
            ["requiredRuntimeFeatures"] = original["requiredRuntimeFeatures"]?.DeepClone() ?? new JsonArray(),
            ["signedSession"] = original["signedSession"]?.DeepClone(),
            ["compatibility"] = Marker,
            ["spotiflacManifest"] = original.DeepClone()
        };
        return ReconcileMetadataHooks(normalized.ToJsonString(), indexJs);
    }

    public static string ReconcileMetadataHooks(string normalizedJson, string indexJs)
    {
        var root = JsonNode.Parse(normalizedJson)!.AsObject();
        if (Text(root, "compatibility") != Marker) return normalizedJson;
        var capabilities = root["capabilities"]!.AsArray();
        var metadata = capabilities.OfType<JsonObject>().SingleOrDefault(item => Text(item, "kind") == "Metadata");
        if (metadata == null) return normalizedJson;
        var exports = ExportedHooks(indexJs);
        var hooks = new[] { "searchTracks", "getTrack", "searchAlbums", "getAlbum", "searchArtists", "getArtist" }
            .Where(hook => exports.Contains(hook) ||
                exports.Contains("customSearch") && (hook.StartsWith("search", StringComparison.Ordinal) || hook == "getTrack"))
            .ToArray();
        if (hooks.Length == 0) capabilities.Remove(metadata);
        else metadata["hooks"] = new JsonArray(hooks.Select(hook => (JsonNode?)JsonValue.Create(hook)).ToArray());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static HashSet<string> ExportedHooks(string indexJs)
    {
        var nodes = new List<Node>();
        var pending = new Stack<Node>();
        pending.Push(new Parser().ParseScript(indexJs));
        while (pending.TryPop(out var node))
        {
            nodes.Add(node);
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        var variables = nodes.OfType<VariableDeclarator>()
            .Where(item => item.Id is Identifier && item.Init != null)
            .GroupBy(item => ((Identifier)item.Id).Name)
            .ToDictionary(group => group.Key, group => group.First().Init!);
        var functions = nodes.OfType<FunctionDeclaration>().Where(item => item.Id != null)
            .Select(item => item.Id!.Name).ToHashSet(StringComparer.Ordinal);
        bool IsFunction(Node? value)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (value is Identifier identifier && visited.Add(identifier.Name))
            {
                if (functions.Contains(identifier.Name)) return true;
                value = variables.GetValueOrDefault(identifier.Name);
            }
            return value is FunctionExpression or ArrowFunctionExpression;
        }
        var hooks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in nodes.OfType<CallExpression>().Where(item => item.Callee is Identifier { Name: "registerExtension" }))
        {
            Node? value = call.Arguments.Count > 0 ? call.Arguments[0] : null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (value is Identifier identifier && visited.Add(identifier.Name))
                value = variables.GetValueOrDefault(identifier.Name);
            if (value is not ObjectExpression exported) continue;
            foreach (var property in exported.Properties.OfType<ObjectProperty>())
            {
                var key = property.Key is Identifier name ? name.Name : (property.Key as StringLiteral)?.Value;
                if (key != null && IsFunction(property.Value))
                    hooks.Add(key);
            }
        }
        return hooks;
    }

    public static string SettingsJson(string normalizedJson)
    {
        using var document = JsonDocument.Parse(normalizedJson);
        var values = new Dictionary<string, object?>();
        if (document.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Array)
        {
            foreach (var setting in settings.EnumerateArray())
            {
                if (!setting.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                    !setting.TryGetProperty("default", out var defaultValue)) continue;
                values[key.GetString()!] = defaultValue.ValueKind switch
                {
                    JsonValueKind.String => defaultValue.GetString(),
                    JsonValueKind.Number when defaultValue.TryGetInt64(out var number) => number,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
        }
        return JsonSerializer.Serialize(values);
    }

    private static JsonObject Capability(string kind, bool settingsAvailable, bool settingsRequired, params string[] hooks) => new()
    {
        ["kind"] = kind,
        ["hooks"] = new JsonArray(hooks.Select(hook => (JsonNode?)JsonValue.Create(hook)).ToArray()),
        ["accountScopes"] = settingsAvailable
            ? new JsonArray("Shared", "Personal")
            : new JsonArray(),
        ["accountRequired"] = settingsRequired
    };

    private static JsonObject Permission(string kind, string value) => new()
    {
        ["kind"] = kind,
        ["value"] = value,
        ["required"] = true
    };

    private static string Text(JsonObject value, string key, string fallback = "") =>
        value[key]?.GetValue<string>() ?? fallback;

    // Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/extensions/src/prelude.js.
    internal const string RuntimePreludeScript = """
        (function(host) {
            const symbolDescription = Object.getOwnPropertyDescriptor(Symbol.prototype, "description").get;
            const stringApply = Reflect.apply;
            const primitiveSymbol = Symbol.toPrimitive;
            function isPrimitive(value) {
                return value === null || (typeof value !== "object" && typeof value !== "function");
            }
            function goString(value) {
                // Goja Value.String accepts a Symbol returned by an object's coercion.
                if (!isPrimitive(value)) {
                    const exotic = value[primitiveSymbol];
                    if (exotic !== undefined && exotic !== null) {
                        value = stringApply(exotic, value, ["string"]);
                        if (!isPrimitive(value)) throw new TypeError("cannot convert object to primitive value");
                    } else {
                        let converted = false;
                        for (const key of ["toString", "valueOf"]) {
                            const method = value[key];
                            if (typeof method !== "function") continue;
                            const result = stringApply(method, value, []);
                            if (isPrimitive(result)) { value = result; converted = true; break; }
                        }
                        if (!converted) throw new TypeError("cannot convert object to primitive value");
                    }
                }
                if (typeof value === "symbol") return (stringApply(symbolDescription, value, []) || "").toWellFormed();
                return String(value).toWellFormed();
            }
            function isMap(value) {
                return value !== null && typeof value === "object" && !Array.isArray(value)
                    && !ArrayBuffer.isView(value) && !(value instanceof Date)
                    && !(value instanceof Map) && !(value instanceof Set);
            }
            function formatGo(value) {
                if (value === null || value === undefined) return "<nil>";
                if (typeof value === "number") {
                    if (Number.isNaN(value)) return "NaN";
                    if (!Number.isFinite(value)) return value < 0 ? "-Inf" : "+Inf";
                    if (Object.is(value, -0)) return "-0";
                }
                if (Array.isArray(value) || ArrayBuffer.isView(value)) {
                    return "[" + Array.from(value, formatGo).join(" ") + "]";
                }
                if (isMap(value)) {
                    return "map[" + Object.keys(value).sort()
                        .map(key => goString(key) + ":" + formatGo(value[key])).join(" ") + "]";
                }
                return goString(value);
            }
            // Adapted from SpotiFLAC-Mobile (MIT), prelude.js bodyString/serialize.
            function signedBody(value) {
                if (value == null) return "";
                if (typeof value === "string") return goString(value);
                if (!isMap(value) && !Array.isArray(value)) return goString(value);
                function quote(text) {
                    return JSON.stringify(goString(text)).replace(/[<>&\u2028\u2029]/g,
                        c => "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0"));
                }
                function compareKeys(a, b) {
                    const left = Array.from(a), right = Array.from(b);
                    for (let i = 0; i < Math.min(left.length, right.length); i++) {
                        const difference = left[i].codePointAt(0) - right[i].codePointAt(0);
                        if (difference) return difference;
                    }
                    return left.length - right.length;
                }
                const ancestors = [];
                function encode(item) {
                    if (item == null) return "null";
                    if (typeof item === "string") return quote(item);
                    if (typeof item === "boolean") return String(item);
                    if (typeof item === "number") {
                        if (!Number.isFinite(item)) throw new TypeError("unsupported JSON number");
                        return Object.is(item, -0) ? "-0" : String(item);
                    }
                    if (typeof item !== "object" || item instanceof Map) throw new TypeError("unsupported JSON value");
                    if (ancestors.length >= 128 || ancestors.includes(item)) throw new TypeError("cyclic or deeply nested JSON value");
                    ancestors.push(item);
                    const result = Array.isArray(item) || ArrayBuffer.isView(item) || item instanceof Set
                        ? "[" + Array.from(item, encode).join(",") + "]"
                        : "{" + Object.keys(item).sort(compareKeys).map(key => quote(key) + ":" + encode(item[key])).join(",") + "}";
                    ancestors.pop();
                    return result;
                }
                return encode(value);
            }
            if (_allstarrSpotiFlacCompatibility) {
                session.signedFetch = function(method, path, body, headers) {
                    if (arguments.length < 2) return {ok: false, error: "method and path are required"};
                    try {
                        const values = Object.create(null);
                        if (isMap(headers)) for (const key of Object.keys(headers)) values[goString(key)] = formatGo(headers[key]);
                        return host.SessionSignedFetch(goString(method), goString(path), signedBody(body), values);
                    } catch (error) { return {ok: false, error: goString(error)}; }
                };
            }
            const responseByteArrays = new WeakMap();
            function binaryOptions(value) {
                if (value === null || typeof value !== "object" || Array.isArray(value) || ArrayBuffer.isView(value)
                    || value instanceof ArrayBuffer || value instanceof Date || value instanceof Map || value instanceof Set) return null;
                const result = Object.create(null);
                for (const key of Object.keys(value)) {
                    let item = value[key];
                    if (typeof item === "string") item = goString(item);
                    else if (responseByteArrays.has(item)) item = responseByteArrays.get(item);
                    else if (item instanceof Uint8ClampedArray) item = new Uint8Array(item.buffer, item.byteOffset, item.byteLength);
                    if (key === "segments" && Array.isArray(item)) item = Array.from(item, binaryOptions);
                    result[key] = item;
                }
                return result;
            }
            function binaryPayload(value) {
                if (typeof value === "string") return goString(value);
                if (responseByteArrays.has(value)) return responseByteArrays.get(value);
                if (value instanceof ArrayBuffer) return new Uint8Array(value);
                if (value instanceof Uint8ClampedArray) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
                return value;
            }
                globalThis.ffmpeg = {
                    getInfo(path) {
                        if (!arguments.length) return {success: false, error: "file path is required"};
                        return {success: false, error: 'media inspection is unavailable'};
                    },
                    convert(input, output, options) {
                        if (arguments.length < 2) return {success: false, error: "input and output paths are required"};
                        return host.FfmpegConvert(goString(input), goString(output), JSON.stringify(binaryOptions(options)));
                    }
                };
                globalThis.ffmpeg.execute = function() {
                    return {success: false, error: "raw FFmpeg execution is disabled; use ffmpeg.convert"};
                };
                function fileCall(method, args) {
                    const paired = ["copy", "move", "transformPatternedBlocks"].includes(method);
                    const write = method === "write" || method === "writeBytes";
                    const required = method === "transformPatternedBlocks" ? 3 : paired || write ? 2 : 1;
                    if (args.length < required) {
                        if (method === "exists") return false;
                        return {success: false, error: method === "transformPatternedBlocks"
                            ? "input path, output path, and options are required"
                            : paired ? "source and destination paths are required"
                            : write ? "path and data are required" : "path is required"};
                    }
                    const value = paired || method === "write" ? goString(args[1])
                        : method === "writeBytes" ? binaryPayload(args[1]) : undefined;
                    const options = method === "readBytes" ? binaryOptions(args[1])
                        : method === "writeBytes" || method === "transformPatternedBlocks" ? binaryOptions(args[2]) : null;
                    if (args[3] != null && typeof args[3] !== "function") return {success: false, error: "progress callback must be a function"};
                    return host.Transform(goString(args[0]), value, JSON.stringify(options), args[3] || null);
                }
                globalThis.file.downloadSegments = function(segments, path, options) {
                    const failure = (error_type, error) => ({success: false, error, error_type, attempts: 0});
                    if (arguments.length < 2) return failure("invalid_request", "segments and output path are required");
                    if (!Array.isArray(segments) || !segments.length) return failure("invalid_request", "segments must be a non-empty array");
                    const opts = binaryOptions(options);
                    const common = Object.create(null);
                    function headersInto(target, source) {
                        if (source && typeof source === "object" && !Array.isArray(source)
                            && !ArrayBuffer.isView(source) && !(source instanceof ArrayBuffer)
                            && !(source instanceof Date) && !(source instanceof Map) && !(source instanceof Set)) {
                            for (const key of Object.keys(source)) target[goString(key)] = formatGo(source[key]);
                        }
                    }
                    if (opts) headersInto(common, opts.headers);
                    const specs = [];
                    for (let index = 0; index < segments.length; index++) {
                        const item = segments[index];
                        const headers = Object.assign(Object.create(null), common);
                        let url;
                        if (typeof item === "string") url = item;
                        else if (item && typeof item === "object" && !Array.isArray(item)
                            && !ArrayBuffer.isView(item) && !(item instanceof ArrayBuffer)
                            && !(item instanceof Date) && !(item instanceof Map) && !(item instanceof Set)) {
                            url = typeof item.url === "string" ? item.url : "";
                            headersInto(headers, item.headers);
                        } else return failure("invalid_request", "segment " + index + " must be a URL string or object");
                        specs.push({url: goString(url), headers});
                    }
                    return host.DownloadSegments(JSON.stringify(specs), goString(path), JSON.stringify(opts), opts && typeof opts.onProgress === "function" ? opts.onProgress : null);
                };
                globalThis.file.transformPatternedBlocks = function() { return fileCall("transformPatternedBlocks", arguments); };
            globalThis.matching = {
                compareStrings(first, second) {
                    return arguments.length < 2 ? 0 : host.CompareStrings(goString(first), goString(second));
                },
                compareDuration(first, second, tolerance) {
                    return arguments.length < 2 ? false : host.CompareDuration(+first, +second, tolerance === undefined ? 3000 : +tolerance);
                },
                normalizeString(value) { return arguments.length ? host.Normalize(goString(value)) : ""; }
            };
                globalThis.gobackend = null;
                utils.getResolutionRemainingMs = function() { return host.ResolutionRemainingMs(); };
                })(host);
        """;

    public const string RuntimeAdapterScript = """
        var _spotiflacExtension = _registeredExtension;
        var _sfManifest = JSON.parse(_allstarrSpotiFlacManifestJson);
        var _sfQualitySelections = (function(options) {
          options = options.map(function(option) {
            return typeof option === 'string' ? { id: option, kind: '' }
              : { id: option.id || option.value || option.name, kind: String(option.kind || '').toLowerCase() };
          }).filter(function(option) { return option.kind !== 'spatial'; });
          var first = options[0] || { id: 'best' };
          var last = options[options.length - 1] || first;
          var lossless = options.filter(function(option) { return option.kind === 'lossless'; });
          var lossy = options.filter(function(option) { return option.kind === 'lossy'; });
          var declaredPlain = options.filter(function(option) { return /^(LOSSLESS|FLAC|CD)$/i.test(option.id); })[0];
          var plain = declaredPlain || lossless[lossless.length - 1] || first;
          var highest = lossless[0] || first;
          var highSelection = { id: highest.id, fallback: lossless.length && plain.id !== highest.id ? plain.id : null,
            streaming: declaredPlain && declaredPlain.id !== highest.id ? declaredPlain.id : null };
          return {
            HighResolution: highSelection, Any: highSelection,
            Lossless: { id: plain.id }, Lossy: { id: (lossy[0] || first).id },
            DataSaver: { id: (lossy[lossy.length - 1] || last).id }
          };
        })(JSON.parse(_allstarrQualityOptionsJson));
        function _sfDownloadError(value) {
          return value instanceof Error ? value
            : new Error(typeof value === 'string' ? value : value && (value.error_message || value.error) || 'SpotiFLAC download failed');
        }
        function _sfStopQualityFallback(error) {
          var reason = [error && error.error_type, error && error.code, error && error.error,
            error && error.error_message, error && error.message, error && error.name, String(error)].join(' ');
          return error && (error.needsVerification === true || error.cancelled === true || error.canceled === true)
            || /\b(?:verify|verification)[ _-]*required\b|\b(?:cancel(?:led|ed|lation)?|AbortError)\b|\b(?:not[ _-]*found|track[ _-]+unavailable)\b|\bHTTP\s*[:=]?\s*(?:404|410)\b/i.test(reason);
        }
        function _sfQualitySelection(request) {
          var selection = _sfQualitySelections[request.requestedQuality] || _sfQualitySelections.Any;
          return request.prepareForStreaming && selection.streaming
            ? { id: selection.streaming, streamingDowngrade: true } : selection;
        }
        var _allstarrInitializedSettings = null;
        function _allstarrInitialize(settings) {
          var fingerprint = JSON.stringify(settings);
          if (fingerprint === _allstarrInitializedSettings) return true;
          var result = _spotiflacExtension.initialize(settings);
          _allstarrInitializedSettings = fingerprint;
          return result;
        }
        function _allstarrPrepareInvocation() {
          if (!_spotiflacExtension || typeof _spotiflacExtension.initialize !== 'function') return;
          var settings = {};
          var defaults = _allstarrDefaultSettings || {};
          for (var defaultKey in defaults) if (Object.prototype.hasOwnProperty.call(defaults, defaultKey)) settings[defaultKey] = defaults[defaultKey];
          var keys = _allstarrSettingKeys || [];
          for (var i = 0; i < keys.length; i++) {
            var value = host.SettingGet(keys[i]);
            if (value === null || value === undefined) continue;
            try { settings[keys[i]] = JSON.parse(String(value)); }
            catch (_) { settings[keys[i]] = String(value); }
          }
          _allstarrInitialize(settings);
        }
        function _sfArray(value) {
          if (Array.isArray(value)) return value;
          if (!value) return [];
          return value.items || value.tracks || value.results || [];
        }
        function _sfArtwork(value) {
          if (!value) return null;
          var image = value.cover_url || value.coverUrl || value.image_url || value.imageUrl || value.cover || value.images;
          if (Array.isArray(image)) image = image.length ? (image[0].url || image[0]) : null;
          if (image && typeof image === 'object') image = image.url || image.src;
          return image ? String(image) : null;
        }
        function _sfArtists(value) {
          var source = value || {};
          var fallbackId = source.artist_id || source.artistId || null;
          value = source.artists || source.artist || [];
          if (!Array.isArray(value)) value = String(value || '').split(',');
          return value.map(function(item, index) {
            return typeof item === 'object'
              ? { name: String(item.name || item.title || ''), id: item.id || item.artist_id || item.artistId || item.browse_id || item.browseId || null }
              : { name: String(item).trim(), id: index === 0 && fallbackId != null ? String(fallbackId) : null };
          }).filter(function(item) { return item.name; });
        }
        function _sfTrack(value) {
          value = value && (value.track || value) || {};
          var album = value.album && typeof value.album === 'object' ? value.album : {};
          return {
            id: String(value.id || value.track_id || ''),
            title: String(value.name || value.title || ''),
            artists: _sfArtists(value),
            albumId: value.album_id || value.albumId || album.id || album.browse_id || album.browseId || null,
            albumTitle: String(value.album_name || value.albumTitle || album.name || album.title || '').trim() || null,
            durationMs: Number(value.duration_ms || value.durationMs || 0),
            spotify_id: value.spotify_id, tidal_id: value.tidal_id, qobuz_id: value.qobuz_id, deezer_id: value.deezer_id,
            isrc: value.isrc ? String(value.isrc) : null,
            isExplicit: value.explicit == null ? null : Boolean(value.explicit),
            artworkUrl: _sfArtwork(value),
            snapshotVersion: null
          };
        }
        var _sfTrackCache = Object.create(null);
        var _sfTrackCacheOrder = [];
        function _sfRememberTrack(track) {
          if (!track || !track.id) return track;
          var id = String(track.id);
          if (!Object.prototype.hasOwnProperty.call(_sfTrackCache, id)) _sfTrackCacheOrder.push(id);
          _sfTrackCache[id] = track;
          while (_sfTrackCacheOrder.length > 512) delete _sfTrackCache[_sfTrackCacheOrder.shift()];
          return track;
        }
        var _sfPrepared = Object.create(null);
        var _sfPreparedOrder = [];
        function _sfForgetPrepared(sourceId) {
          _sfPreparedOrder = _sfPreparedOrder.filter(function(id) {
            if (_sfPrepared[id].sourceId !== String(sourceId)) return true;
            delete _sfPrepared[id];
            return false;
          });
        }
        function _sfRememberPrepared(sourceId, trackId, context) {
          var id = String(trackId || '');
          if (!id) return;
          if (!context || typeof context !== 'object' || Array.isArray(context)) return;
          var json;
          try { json = JSON.stringify(context); } catch (error) { return; }
          if (!json || json.length > 262144) return;
          if (!Object.prototype.hasOwnProperty.call(_sfPrepared, id)) _sfPreparedOrder.push(id);
          _sfPrepared[id] = { json: json, at: Date.now(), sourceId: String(sourceId) };
          while (_sfPreparedOrder.length > 256) delete _sfPrepared[_sfPreparedOrder.shift()];
        }
        function _sfPreparedFor(trackId, sourceId) {
          var entry = _sfPrepared[String(trackId || '')];
          if (!entry || entry.sourceId !== String(sourceId) || Date.now() - entry.at > 900000) return {};
          try { return JSON.parse(entry.json); } catch (error) { return {}; }
        }
        function _sfPreparedIdFor(sourceId) {
          return Object.keys(_sfPrepared).filter(function(id) {
            var entry = _sfPrepared[id];
            return entry.sourceId === String(sourceId) && Date.now() - entry.at <= 900000;
          })[0];
        }
        function _sfDownloadTrack(request) {
          if (request.track && typeof request.track === 'object') return request.track;
          var track = null;
          try {
            if (typeof _spotiflacExtension.getTrack === 'function') track = _spotiflacExtension.getTrack(request.trackId);
          } catch (_) {}
          track = track || _sfTrackCache[String(request.trackId)] || {};
          return Object.assign({}, track, { id: track.id || request.trackId, name: track.name || track.title || '',
            duration_ms: Number(track.duration_ms || track.durationMs || 0) });
        }
        function _sfAvailabilityOptions(request, track) {
          var selection = _sfQualitySelection(request);
          var options = { requestedQuality: request.requestedQuality, quality: selection.id, track: track, duration_ms: track.duration_ms };
          options.spotify_id = String(track.spotify_id || '').trim();
          ['tidal', 'qobuz', 'deezer'].forEach(function(provider) {
            var key = provider + '_id';
            options[key] = String(track[key] || '').trim();
            var prefix = provider + ':';
            var id = String(request.trackId || '').trim();
            if (!options[key] && id.toLowerCase().indexOf(prefix) === 0) options[key] = id.substring(prefix.length);
          });
          if (!request.resolveRecording && !options.spotify_id && !options.tidal_id && !options.qobuz_id && !options.deezer_id)
            options.spotify_id = String(request.trackId || '').trim();
          return options;
        }
        function _sfPreferredTrackId(options, explicit, sourceId) {
          explicit = String(explicit || '').trim();
          if (explicit) return explicit;
          var spotifyId = options.spotify_id || String(sourceId || '').trim();
          var replaces = (_sfManifest.capabilities || {}).replacesBuiltInProviders;
          replaces = Array.isArray(replaces) ? replaces.filter(function(value) { return typeof value === 'string'; })
            .map(function(value) { return value.trim().toLowerCase(); }) : [];
          var providers = ['tidal', 'qobuz', 'deezer', 'spotify'];
          for (var i = 0; i < providers.length; i++) {
            var provider = providers[i];
            if (replaces.indexOf(provider) < 0) continue;
            var nativeId = options[provider + '_id'];
            if (provider !== 'spotify' && nativeId) return nativeId;
            var id = spotifyId;
            if (id) {
              var prefix = provider + ':';
              return id.toLowerCase().indexOf(prefix) === 0 ? id.substring(prefix.length) : id;
            }
          }
          return spotifyId || options.tidal_id || options.qobuz_id || options.deezer_id || '';
        }
        function _sfCheckAvailability(request, options) {
          var track = options.track;
          var artists = _sfArtists(track).map(function(item) { return item.name; }).join(', ');
          _sfForgetPrepared(request.trackId);
          var result = _spotiflacExtension.checkAvailability(track.isrc || '', track.name || '', artists, options);
          if (result && result.available !== false) {
            var id = _sfPreferredTrackId(options, result.track_id, request.trackId);
            var context = result.prepared_context || result.preparedContext;
            if (!context || typeof context !== 'object' || Array.isArray(context)) context = {};
            _sfRememberPrepared(request.trackId, id, Object.assign({}, context, { host_track: track }));
            if (request.resolveRecording) _sfRememberPrepared(id, id, Object.assign({}, context, { host_track: track }));
          }
          return result;
        }
        function _sfDownloadOptions(request, trackId, track) {
          var prepared = _sfPreparedFor(trackId, request.trackId);
          prepared.host_track = track || prepared.host_track || _sfDownloadTrack(request);
          return { preparedContext: prepared, resolutionTimeoutMs: utils.getResolutionRemainingMs() };
        }
        function _sfAlbum(value) {
          value = value && (value.album || value) || {};
          return { id: String(value.id || ''), title: String(value.name || value.title || ''), artists: _sfArtists(value),
            trackCount: Number(value.total_tracks || (value.tracks && value.tracks.length) || 0), artworkUrl: _sfArtwork(value), snapshotVersion: null };
        }
        function _sfArtist(value) {
          value = value && (value.artist || value) || {};
          return { id: String(value.id || ''), name: String(value.name || value.title || ''), artworkUrl: _sfArtwork(value), snapshotVersion: null };
        }
        function _sfSearch(request, filter, mapper) {
          // As in SpotiFLAC-Mobile: typed search hooks take (query, limit) and return {tracks|albums|artists}; customSearch takes options.
          var typed = _spotiflacExtension['search' + filter.charAt(0).toUpperCase() + filter.slice(1)];
          var limit = request.page && request.page.limit || 20;
          var result;
          if (typeof typed === 'function') {
            result = typed.call(_spotiflacExtension, request.query || '', limit);
            if (result && !Array.isArray(result) && Array.isArray(result[filter])) result = result[filter];
          } else if (typeof _spotiflacExtension.customSearch === 'function') {
            result = _spotiflacExtension.customSearch(request.query || '', { limit: limit, filter: filter.replace(/s$/, '') });
          } else return { items: [], isPartial: false };
          // Typed hooks may delegate to an unfiltered customSearch, so results are always filtered by type ("song" counts as a track).
          var singular = filter.replace(/s$/, '');
          var items = _sfArray(result).filter(function(item) {
            var type = String(item.item_type || item.type || '').toLowerCase();
            return !type || type === filter || type === singular || (singular === 'track' && (type === 'song' || type === 'songs'));
          }).map(mapper).filter(function(item) { return item.id; });
          if (filter === 'tracks') items.forEach(_sfRememberTrack);
          return { items: items, nextCursor: null, isPartial: false, snapshotVersion: null };
        }
        function _sfPlaylist(value) {
          value = value && (value.playlist || value) || {};
          return { id: String(value.id || ''), name: String(value.name || value.title || ''),
            owner: { providerUserId: String(value.owner_id || value.owner || 'spotiflac'), displayName: String(value.owner_name || value.owner || 'SpotiFLAC') },
            sourceRevision: String(value.revision || value.snapshot_id || '1'), description: String(value.description || ''), artworkUrl: _sfArtwork(value),
            trackCount: Number(value.total_tracks || (value.tracks && value.tracks.length) || 0), sourceETag: null };
        }
        _registeredExtension = {
          initialize: function(settings) { return typeof _spotiflacExtension.initialize === 'function' ? _allstarrInitialize(settings || {}) : true; },
          searchTracks: function(request) { return _sfSearch(request, 'tracks', _sfTrack); },
          getTrack: function(request) {
            var id = String(request.id);
            if (Object.prototype.hasOwnProperty.call(_sfTrackCache, id)) return _sfTrackCache[id];
            if (typeof _spotiflacExtension.getTrack === 'function') return _sfRememberTrack(_sfTrack(_spotiflacExtension.getTrack(id)));
            var page = _sfSearch({ query: request.id, page: { limit: 10 } }, 'tracks', _sfTrack);
            return page.items.filter(function(item) { return item.id === id; })[0] || null;
          },
          searchAlbums: function(request) { return _sfSearch(request, 'albums', _sfAlbum); },
          getAlbum: function(request) { return _sfAlbum(typeof _spotiflacExtension.getAlbum === 'function' ? _spotiflacExtension.getAlbum(request.id) : null); },
          searchArtists: function(request) { return _sfSearch(request, 'artists', _sfArtist); },
          getArtist: function(request) { return _sfArtist(typeof _spotiflacExtension.getArtist === 'function' ? _spotiflacExtension.getArtist(request.id) : null); },
          getUserPlaylists: function() { return { items: [], nextCursor: null, isPartial: false, snapshotVersion: null }; },
          searchPlaylists: function(request) { return _sfSearch(request, 'playlists', _sfPlaylist); },
          getPlaylistTracks: function(request) {
            var raw = typeof _spotiflacExtension.getPlaylist === 'function' ? _spotiflacExtension.getPlaylist(request.playlistId) : null;
            var playlist = _sfPlaylist(raw);
            var tracks = _sfArray(raw && (raw.tracks || (raw.playlist && raw.playlist.tracks))).map(function(item, index) {
              var track = _sfTrack(item); return { position: index, trackId: track.id, canonicalRecordingId: null, metadata: track };
            });
            return { playlist: playlist, tracks: { items: tracks, nextCursor: null, isPartial: false, snapshotVersion: playlist.sourceRevision } };
          },
          fetchLyrics: function(request) {
            if (typeof _spotiflacExtension.fetchLyrics !== 'function') return { availability: 'Unavailable', source: 'SpotiFLAC' };
            var track = null;
            var title = String(request.trackTitle || '');
            var artists = Array.isArray(request.artistNames) ? request.artistNames.join(', ') : '';
            var album = String(request.albumTitle || '');
            var duration = Number(request.durationSeconds || 0);
            if (!title && typeof _spotiflacExtension.getTrack === 'function') {
              track = _spotiflacExtension.getTrack(request.providerTrackId);
              if (track) {
                title = String(track.name || track.title || '');
                artists = _sfArtists(track).map(function(item) { return item.name; }).join(', ');
                album = String(track.album_name || '');
                duration = Number(track.duration_ms || 0) / 1000;
              }
            }
            if (!title) return { availability: 'Unavailable', source: 'SpotiFLAC' };
            var result = _spotiflacExtension.fetchLyrics(title, artists, album, duration);
            if (!result) return { availability: 'Unavailable', source: 'SpotiFLAC' };
            var lines = Array.isArray(result.lines) ? result.lines : [];
            var timed = lines.filter(function(line) {
              var start = Number(line && (line.startTimeMs !== undefined ? line.startTimeMs : line.start_time_ms));
              return isFinite(start) && start >= 0 && start < 86400000 && String(line.words || line.text || '').trim();
            }).map(function(line) {
              var milliseconds = Number(line.startTimeMs !== undefined ? line.startTimeMs : line.start_time_ms);
              var minutes = Math.floor(milliseconds / 60000);
              var seconds = Math.floor((milliseconds % 60000) / 1000);
              var hundredths = Math.floor((milliseconds % 1000) / 10);
              return '[' + String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0') + '.' + String(hundredths).padStart(2, '0') + ']' + String(line.words || line.text || '').trim();
            }).join('\n');
            var plain = result.plainLyrics || result.plain_lyrics || result.lyrics || result.text || '';
            var content = timed || plain;
            var format = timed ? 'LineTimed' : 'PlainText';
            return content ? { availability: 'Available', source: String(result.provider || 'SpotiFLAC'), format: format, content: String(content), revision: null }
              : { availability: 'Unavailable', source: String(result.provider || 'SpotiFLAC') };
          },
          checkAvailability: function(request) {
            if (typeof _spotiflacExtension.download !== 'function') return { state: 'Incompatible', availableQualities: [] };
            if (typeof _spotiflacExtension.checkAvailability !== 'function') return { state: 'Available', availableQualities: ['Any'] };
            var options = _sfAvailabilityOptions(request, _sfDownloadTrack(request));
            var result = _sfCheckAvailability(request, options);
            if (request.prepareForStreaming && (!result || typeof result.available !== 'boolean')) return;
            return result && result.available !== false
              ? { state: 'Available', availableQualities: ['Any'], estimatedBytes: null,
                  trackId: _sfPreferredTrackId(options, result.track_id, request.resolveRecording ? '' : request.trackId) }
              : { state: 'Unavailable', availableQualities: [], estimatedBytes: null,
                  reason: result && /^[a-z][a-z0-9_]{0,99}$/.test(result.reason || '') ? result.reason : null };
          },
          getStreamLease: function() { return null; },
          probeStream: function(request) {
            var result = this.checkAvailability(request);
            return { available: result.state === 'Available', observedAt: new Date().toISOString() };
          },
          download: function(request) {
            if (typeof _spotiflacExtension.download !== 'function') throw new Error('SpotiFLAC extension has no download hook');
            var safeId = String(request.trackId || 'track').replace(/[^A-Za-z0-9._-]/g, '_');
            var output = safeId + '.audio';
            var selection = _sfQualitySelection(request);
            var quality = selection.id;
            var trackId = _sfPreparedIdFor(request.trackId);
            var track;
            if (!trackId) {
              track = _sfDownloadTrack(request);
              var availabilityOptions = _sfAvailabilityOptions(request, track);
              var availability = null;
              if (typeof _spotiflacExtension.checkAvailability === 'function') {
                try { availability = _sfCheckAvailability(request, availabilityOptions); } catch (_) {}
              }
              var available = availability && availability.available !== false;
              trackId = _sfPreferredTrackId(availabilityOptions, available ? availability.track_id : '', request.trackId);
            }
            var result;
            try {
              result = _spotiflacExtension.download(trackId, quality, output, function() {}, _sfDownloadOptions(request, trackId, track));
              if (result && result.success === false) throw result;
            } catch (error) {
              if (!selection.fallback || _sfStopQualityFallback(error) || utils.getResolutionRemainingMs() < 30000)
                throw _sfDownloadError(error);
              quality = selection.fallback;
              result = _spotiflacExtension.download(trackId, quality, output, function() {}, _sfDownloadOptions(request, trackId, track));
            }
            if (!result || result.success === false) throw _sfDownloadError(result);
            var path = String(result.file_path || result.path || output);
            var extension = String(result.actual_extension || result.output_extension || path.substring(path.lastIndexOf('.')) || '.audio').toLowerCase();
            if (extension.charAt(0) !== '.') extension = '.' + extension;
            var artifactId = safeId + extension;
            var written = host.CommitFile(path, artifactId);
            var codec = String(result.audio_codec || (extension === '.m4a' ? 'aac' : extension.replace('.', '')) || 'unknown');
            var mime = extension === '.flac' ? 'audio/flac' : extension === '.m4a' ? 'audio/mp4' : extension === '.ogg' || extension === '.opus' ? 'audio/ogg' : 'audio/mpeg';
            return { artifactId: written.artifactId, sha256: written.sha256, sizeBytes: written.sizeBytes, verified: written.verified,
              quality: quality, qualityDowngradeReason: selection.streamingDowngrade
                ? 'Streaming uses lossless for faster start'
                : quality !== selection.id ? 'Using ' + quality + ' after ' + selection.id + ' download failed.' : null,
              media: { mimeType: mime, container: extension.replace('.', '') || 'audio', codec: codec,
                sampleRate: Number(result.sample_rate || 0) || null, bitDepth: Number(result.bit_depth || 0) || null } };
          }
        };
        """;
}
