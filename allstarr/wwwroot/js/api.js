// API calls

export async function fetchStatus() {
    const res = await fetch('/api/admin/status');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchPlaylists() {
    const res = await fetch('/api/admin/playlists');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchPlaylistTracks(name) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(name)}/tracks`);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchTrackMappings() {
    const res = await fetch('/api/admin/mappings/tracks');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function deleteTrackMapping(playlist, spotifyId) {
    const res = await fetch(
        `/api/admin/mappings/tracks?playlist=${encodeURIComponent(playlist)}&spotifyId=${encodeURIComponent(spotifyId)}`,
        { method: 'DELETE' }
    );
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to remove mapping');
    }
    return await res.json();
}

export async function fetchDownloads() {
    const res = await fetch('/api/admin/downloads');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function deleteDownload(path) {
    const res = await fetch(`/api/admin/downloads?path=${encodeURIComponent(path)}`, {
        method: 'DELETE'
    });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchConfig() {
    const res = await fetch('/api/admin/config');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function updateConfig(key, value) {
    const res = await fetch('/api/admin/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ key, value })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to update setting');
    }
    return await res.json();
}

export async function fetchJellyfinUsers() {
    const res = await fetch('/api/admin/jellyfin/users');
    if (!res.ok) return null;
    return await res.json();
}

export async function fetchJellyfinPlaylists(userId = null) {
    let url = '/api/admin/jellyfin/playlists';
    if (userId) url += '?userId=' + encodeURIComponent(userId);
    
    const res = await fetch(url);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchSpotifyUserPlaylists() {
    const res = await fetch('/api/admin/spotify/user-playlists');
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to fetch Spotify playlists');
    }
    return await res.json();
}

export async function linkPlaylist(jellyfinId, spotifyId, syncSchedule) {
    const res = await fetch(`/api/admin/jellyfin/playlists/${encodeURIComponent(jellyfinId)}/link`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ spotifyPlaylistId: spotifyId, syncSchedule })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to link playlist');
    }
    return await res.json();
}

export async function unlinkPlaylist(name) {
    const res = await fetch(`/api/admin/jellyfin/playlists/${encodeURIComponent(name)}/unlink`, {
        method: 'DELETE'
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to unlink playlist');
    }
    return await res.json();
}

export async function refreshPlaylists() {
    const res = await fetch('/api/admin/playlists/refresh', { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function clearPlaylistCache(name) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(name)}/clear-cache`, { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function matchPlaylistTracks(name) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(name)}/match`, { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function matchAllPlaylists() {
    const res = await fetch('/api/admin/playlists/match-all', { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function clearCache() {
    const res = await fetch('/api/admin/cache/clear', { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function restartContainer() {
    const res = await fetch('/api/admin/restart', { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function fetchEndpointUsage(top = 50) {
    const res = await fetch(`/api/admin/debug/endpoint-usage?top=${top}`);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function clearEndpointUsage() {
    const res = await fetch('/api/admin/debug/endpoint-usage', { method: 'DELETE' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function addPlaylist(name, spotifyId) {
    const res = await fetch('/api/admin/playlists', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, spotifyId })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to add playlist');
    }
    return await res.json();
}

export async function removePlaylist(name) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(name)}`, {
        method: 'DELETE'
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to remove playlist');
    }
    return await res.json();
}

export async function editPlaylistSchedule(playlistName, syncSchedule) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(playlistName)}/schedule`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ syncSchedule })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to update schedule');
    }
    return await res.json();
}

export async function searchJellyfin(query) {
    const res = await fetch(`/api/admin/jellyfin/search?query=${encodeURIComponent(query)}`);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function getJellyfinTrack(jellyfinId) {
    const res = await fetch(`/api/admin/jellyfin/track/${jellyfinId}`);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function saveTrackMapping(playlistName, mapping) {
    const res = await fetch(`/api/admin/playlists/${encodeURIComponent(playlistName)}/map`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(mapping)
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to save mapping');
    }
    return await res.json();
}

export async function saveLyricsMapping(artist, title, album, durationSeconds, lyricsId) {
    const res = await fetch('/api/admin/lyrics/map', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ artist, title, album, durationSeconds, lyricsId })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to save lyrics mapping');
    }
    return await res.json();
}

export async function updateConfigSetting(key, value) {
    const res = await fetch('/api/admin/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ updates: { [key]: value } })
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to update setting');
    }
    return await res.json();
}

export async function initCookieDate() {
    const res = await fetch('/api/admin/config/init-cookie-date', { method: 'POST' });
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}

export async function exportEnv() {
    const res = await fetch('/api/admin/export-env');
    if (!res.ok) {
        throw new Error('Export failed');
    }
    return await res.blob();
}

export async function importEnv(file) {
    const formData = new FormData();
    formData.append('file', file);
    
    const res = await fetch('/api/admin/import-env', {
        method: 'POST',
        body: formData
    });
    if (!res.ok) {
        const error = await res.json();
        throw new Error(error.error || 'Failed to import .env file');
    }
    return await res.json();
}

export async function getSquidWTFBaseUrl() {
    const res = await fetch('/api/admin/squidwtf-base-url');
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${res.statusText}`);
    }
    return await res.json();
}
