using System.Text.Json;
using System.Text.Json.Nodes;

namespace allstarr.Core.Extensions;

public static class SpotiFlacExtensionCompatibility
{
    public const string Marker = "spotiflac-v1";

    public static bool IsManifest(string json)
    {
        if (!CompatibilitySunsets.SpotiFlacTranslatorEnabled) return false;

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

    public static string NormalizeManifest(string originalJson, string indexJs)
    {
        var original = JsonNode.Parse(originalJson)?.AsObject() ??
                       throw new ExtensionSdkValidationException("SpotiFLAC manifest must be a JSON object.");
        var id = Text(original, "name").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id))
            throw new ExtensionSdkValidationException("SpotiFLAC manifest requires a name.");
        var extensionId = $"spotiflac-{id}";
        var types = original["type"]?.AsArray().Select(item => item?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal) ?? [];
        var hasSettings = original["settings"] is JsonArray { Count: > 0 };
        var requiresSettings = original["settings"] is JsonArray declaredSettings &&
                               declaredSettings.OfType<JsonObject>().Any(item => item["required"]?.GetValue<bool>() == true);
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
        if (original["settings"] is JsonArray settings)
        {
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
            ["settings"] = original["settings"]?.DeepClone() ?? new JsonArray(),
            ["qualityOptions"] = original["qualityOptions"]?.DeepClone() ?? new JsonArray(),
            ["requiredRuntimeFeatures"] = original["requiredRuntimeFeatures"]?.DeepClone() ?? new JsonArray(),
            ["signedSession"] = original["signedSession"]?.DeepClone(),
            ["compatibility"] = Marker,
            ["spotiflacManifest"] = original.DeepClone()
        };
        return normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string SettingsJson(string normalizedJson)
    {
        using var document = JsonDocument.Parse(normalizedJson);
        var values = new Dictionary<string, object?>();
        if (document.RootElement.TryGetProperty("spotiflacManifest", out var original) &&
            original.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Array)
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
            ? new JsonArray("Global", "User", "Library")
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

    public const string RuntimeAdapterScript = """
        var _spotiflacExtension = _registeredExtension;
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
          _spotiflacExtension.initialize(settings);
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
            albumTitle: String(value.album_name || value.albumTitle || album.name || album.title || ''),
            durationMs: Number(value.duration_ms || value.durationMs || 0),
            isrc: value.isrc ? String(value.isrc) : null,
            isExplicit: value.explicit == null ? null : Boolean(value.explicit),
            artworkUrl: _sfArtwork(value),
            snapshotVersion: null
          };
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
          if (!_spotiflacExtension || typeof _spotiflacExtension.customSearch !== 'function') return { items: [], isPartial: false };
          var result = _spotiflacExtension.customSearch(request.query || '', { limit: request.page && request.page.limit || 20, filter: filter });
          var items = _sfArray(result).filter(function(item) {
            var type = String(item.item_type || item.type || '').toLowerCase();
            return !type || type === filter || type === filter.replace(/s$/, '');
          }).map(mapper).filter(function(item) { return item.id; });
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
          initialize: function(settings) { return typeof _spotiflacExtension.initialize === 'function' ? _spotiflacExtension.initialize(settings || {}) : true; },
          searchTracks: function(request) { return _sfSearch(request, 'tracks', _sfTrack); },
          getTrack: function(request) {
            if (typeof _spotiflacExtension.getTrack === 'function') return _sfTrack(_spotiflacExtension.getTrack(request.id));
            var page = _sfSearch({ query: request.id, page: { limit: 10 } }, 'tracks', _sfTrack);
            return page.items.filter(function(item) { return item.id === String(request.id); })[0] || page.items[0] || { id: String(request.id), title: String(request.id), artists: [] };
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
            var track = typeof _spotiflacExtension.getTrack === 'function' ? _spotiflacExtension.getTrack(request.trackId) : null;
            track = track || {};
            var artists = _sfArtists(track).map(function(item) { return item.name; }).join(', ');
            var result = _spotiflacExtension.checkAvailability(track.isrc || '', track.name || track.title || '', artists, { requestedQuality: request.requestedQuality });
            return result && result.available !== false
              ? { state: 'Available', availableQualities: ['Any'], estimatedBytes: null }
              : { state: 'Unavailable', availableQualities: [], estimatedBytes: null };
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
            var quality = request.requestedQuality === 'Lossy' ? 'best' : 'best';
            var result = _spotiflacExtension.download(request.trackId, quality, output, function() {});
            if (!result || result.success === false) throw new Error(result && (result.error_message || result.error) || 'SpotiFLAC download failed');
            var path = String(result.file_path || result.path || output);
            var extension = String(result.actual_extension || result.output_extension || path.substring(path.lastIndexOf('.')) || '.audio').toLowerCase();
            var artifactId = safeId + extension;
            var written = host.CommitFile(path, artifactId);
            var codec = String(result.audio_codec || (extension === '.m4a' ? 'aac' : extension.replace('.', '')) || 'unknown');
            var mime = extension === '.flac' ? 'audio/flac' : extension === '.m4a' ? 'audio/mp4' : extension === '.ogg' || extension === '.opus' ? 'audio/ogg' : 'audio/mpeg';
            return { artifactId: written.artifactId, sha256: written.sha256, sizeBytes: written.sizeBytes, verified: written.verified,
              media: { mimeType: mime, container: extension.replace('.', '') || 'audio', codec: codec,
                sampleRate: Number(result.sample_rate || 0) || null, bitDepth: Number(result.bit_depth || 0) || null } };
          }
        };
        """;
}
