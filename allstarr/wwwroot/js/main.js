// Main entry point - ES6 modules

import { escapeHtml, escapeJs, showToast, formatCookieAge, capitalizeProvider } from './utils.js';
import * as API from './api.js';
import * as UI from './ui.js';
import { openModal, closeModal, setupModalBackdropClose } from './modals.js';
import { viewTracks, openManualMap, openExternalMap, searchJellyfinTracks, selectJellyfinTrack, saveLocalMapping, saveManualMapping, extractJellyfinId, validateExternalMapping, openLyricsMap, saveLyricsMapping, searchProvider } from './helpers.js';

// Global state
let currentEditKey = null;
let currentEditType = null;
let currentEditOptions = null;
let cookieDateInitialized = false;
let restartRequired = false;
let playlistAutoRefreshInterval = null;
let currentLinkMode = 'select';
let spotifyUserPlaylists = [];

// Make functions globally available for onclick handlers
window.showToast = showToast;
window.escapeHtml = escapeHtml;
window.escapeJs = escapeJs;
window.openModal = openModal;
window.closeModal = closeModal;
window.capitalizeProvider = capitalizeProvider;

// Restart banner
window.showRestartBanner = function() {
    restartRequired = true;
    document.getElementById('restart-banner').classList.add('active');
};

window.dismissRestartBanner = function() {
    document.getElementById('restart-banner').classList.remove('active');
};

// Tab switching
window.switchTab = function(tabName) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
    
    const tab = document.querySelector(`.tab[data-tab="${tabName}"]`);
    const content = document.getElementById('tab-' + tabName);
    
    if (tab && content) {
        tab.classList.add('active');
        content.classList.add('active');
        window.location.hash = tabName;
    }
};

// Initialize cookie date
async function initCookieDate() {
    if (cookieDateInitialized) {
        console.log('Cookie date already initialized, skipping');
        return;
    }
    
    cookieDateInitialized = true;
    
    try {
        await API.initCookieDate();
        console.log('Cookie date initialized successfully - restart container to apply');
        showToast('Cookie date set. Restart container to apply changes.', 'success');
    } catch (error) {
        console.error('Failed to init cookie date:', error);
        cookieDateInitialized = false;
    }
}

// Fetch and update status
window.fetchStatus = async function() {
    try {
        const data = await API.fetchStatus();
        UI.updateStatusUI(data);
        
        // Update cookie age
        const cookieAgeEl = document.getElementById('spotify-cookie-age');
        if (cookieAgeEl) {
            const hasCookie = data.spotify.hasCookie;
            const age = formatCookieAge(data.spotify.cookieSetDate, hasCookie);
            cookieAgeEl.innerHTML = `<span class="${age.class}">${age.text}</span><br><small style="color:var(--text-secondary)">${age.detail}</small>`;
            
            if (age.needsInit) {
                console.log('Cookie exists but date not set, initializing...');
                initCookieDate();
            }
        }
    } catch (error) {
        console.error('Failed to fetch status:', error);
        showToast('Failed to fetch status: ' + error.message, 'error');
        UI.showErrorState(error.message);
    }
};

// Fetch playlists
window.fetchPlaylists = async function(silent = false) {
    try {
        const data = await API.fetchPlaylists();
        UI.updatePlaylistsUI(data);
    } catch (error) {
        if (!silent) {
            console.error('Failed to fetch playlists:', error);
            showToast('Failed to fetch playlists', 'error');
        }
    }
};

// Fetch track mappings
window.fetchTrackMappings = async function() {
    try {
        const data = await API.fetchTrackMappings();
        UI.updateTrackMappingsUI(data);
    } catch (error) {
        console.error('Failed to fetch track mappings:', error);
        showToast('Failed to fetch track mappings', 'error');
    }
};

// Delete track mapping
window.deleteTrackMapping = async function(playlist, spotifyId) {
    if (!confirm(`Remove manual external mapping for ${spotifyId} in playlist "${playlist}"?\n\nThis will:\n• Delete the manual mapping from the cache\n• Allow the track to be matched automatically again\n• The track may be re-matched with potentially better results\n\nThis action cannot be undone.`)) {
        return;
    }
    
    try {
        await API.deleteTrackMapping(playlist, spotifyId);
        showToast('Mapping removed successfully', 'success');
        await window.fetchTrackMappings();
    } catch (error) {
        console.error('Failed to delete mapping:', error);
        showToast(error.message || 'Failed to remove mapping', 'error');
    }
};

// Fetch missing tracks
window.fetchMissingTracks = async function() {
    try {
        const data = await API.fetchPlaylists();
        const tbody = document.getElementById('missing-tracks-table-body');
        const missingTracks = [];
        
        // Collect all missing tracks from all playlists
        for (const playlist of data.playlists) {
            if (playlist.externalMissing > 0) {
                try {
                    const tracksData = await API.fetchPlaylistTracks(playlist.name);
                    const missing = tracksData.tracks.filter(t => t.isLocal === null);
                    missing.forEach(t => {
                        missingTracks.push({
                            playlist: playlist.name,
                            ...t
                        });
                    });
                } catch (err) {
                    console.error(`Failed to fetch tracks for ${playlist.name}:`, err);
                }
            }
        }
        
        // Update summary
        document.getElementById('missing-total').textContent = missingTracks.length;
        
        if (missingTracks.length === 0) {
            tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;color:var(--text-secondary);padding:40px;">🎉 No missing tracks! All tracks are matched.</td></tr>';
            return;
        }
        
        tbody.innerHTML = missingTracks.map(t => {
            const artist = (t.artists && t.artists.length > 0) ? t.artists.join(', ') : '';
            const searchQuery = `${t.title} ${artist}`;
            return `
                <tr>
                    <td><strong>${escapeHtml(t.playlist)}</strong></td>
                    <td>${escapeHtml(t.title)}</td>
                    <td>${escapeHtml(artist)}</td>
                    <td style="color:var(--text-secondary);">${t.album ? escapeHtml(t.album) : '-'}</td>
                    <td>
                        <button onclick="searchProvider('${escapeJs(searchQuery)}', 'squidwtf')" 
                            style="margin-right:4px;font-size:0.75rem;padding:4px 8px;background:#3b82f6;border-color:#3b82f6;color:white;">🔍 Search</button>
                        <button onclick="openMapToLocal('${escapeJs(t.playlist)}', '${escapeJs(t.spotifyId)}', '${escapeJs(t.title)}', '${escapeJs(artist)}')" 
                            style="margin-right:4px;font-size:0.75rem;padding:4px 8px;background:var(--success);border-color:var(--success);">Map to Local</button>
                        <button onclick="openMapToExternal('${escapeJs(t.playlist)}', '${escapeJs(t.spotifyId)}', '${escapeJs(t.title)}', '${escapeJs(artist)}')" 
                            style="font-size:0.75rem;padding:4px 8px;background:var(--warning);border-color:var(--warning);">Map to External</button>
                    </td>
                </tr>
            `;
        }).join('');
    } catch (error) {
        console.error('Failed to fetch missing tracks:', error);
        showToast('Failed to fetch missing tracks', 'error');
    }
};

// Fetch downloads
window.fetchDownloads = async function() {
    try {
        const data = await API.fetchDownloads();
        const tbody = document.getElementById('downloads-table-body');
        
        document.getElementById('downloads-count').textContent = data.count;
        document.getElementById('downloads-size').textContent = data.totalSizeFormatted;
        
        if (data.count === 0) {
            tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;color:var(--text-secondary);padding:40px;">No downloaded files found.</td></tr>';
            return;
        }
        
        tbody.innerHTML = data.files.map(f => {
            return `
                <tr data-path="${escapeHtml(f.path)}">
                    <td><strong>${escapeHtml(f.artist)}</strong></td>
                    <td>${escapeHtml(f.album)}</td>
                    <td style="font-family:monospace;font-size:0.85rem;">${escapeHtml(f.fileName)}</td>
                    <td style="color:var(--text-secondary);">${f.sizeFormatted}</td>
                    <td>
                        <button onclick="downloadFile('${escapeJs(f.path)}')" 
                            style="margin-right:4px;font-size:0.75rem;padding:4px 8px;background:var(--accent);border-color:var(--accent);">Download</button>
                        <button onclick="deleteDownload('${escapeJs(f.path)}')" 
                            class="danger" style="font-size:0.75rem;padding:4px 8px;">Delete</button>
                    </td>
                </tr>
            `;
        }).join('');
    } catch (error) {
        console.error('Failed to fetch downloads:', error);
        showToast('Failed to fetch downloads', 'error');
    }
};

window.downloadFile = function(path) {
    try {
        window.open(`/api/admin/downloads/file?path=${encodeURIComponent(path)}`, '_blank');
    } catch (error) {
        console.error('Failed to download file:', error);
        showToast('Failed to download file', 'error');
    }
};

window.downloadAllKept = function() {
    try {
        window.open('/api/admin/downloads/all', '_blank');
        showToast('Preparing download archive...', 'info');
    } catch (error) {
        console.error('Failed to download all files:', error);
        showToast('Failed to download all files', 'error');
    }
};

window.deleteDownload = async function(path) {
    if (!confirm(`Delete this file?\n\n${path}\n\nThis action cannot be undone.`)) {
        return;
    }
    
    try {
        await API.deleteDownload(path);
        showToast('File deleted successfully', 'success');
        
        const escapedPath = path.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
        const row = document.querySelector(`tr[data-path="${escapedPath}"]`);
        if (row) row.remove();
        
        await window.fetchDownloads();
    } catch (error) {
        console.error('Failed to delete file:', error);
        showToast(error.message || 'Failed to delete file', 'error');
    }
};

// Fetch config
window.fetchConfig = async function() {
    try {
        const data = await API.fetchConfig();
        UI.updateConfigUI(data);
    } catch (error) {
        console.error('Failed to fetch config:', error);
    }
};

// Fetch Jellyfin playlists
window.fetchJellyfinPlaylists = async function() {
    const tbody = document.getElementById('jellyfin-playlist-table-body');
    tbody.innerHTML = '<tr><td colspan="6" class="loading"><span class="spinner"></span> Loading Jellyfin playlists...</td></tr>';
    
    try {
        const userId = document.getElementById('jellyfin-user-select')?.value;
        const data = await API.fetchJellyfinPlaylists(userId);
        UI.updateJellyfinPlaylistsUI(data);
    } catch (error) {
        console.error('Failed to fetch Jellyfin playlists:', error);
        tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:var(--error);padding:40px;">Failed to fetch playlists</td></tr>';
    }
};

// Fetch Jellyfin users
window.fetchJellyfinUsers = async function() {
    try {
        const data = await API.fetchJellyfinUsers();
        if (data) {
            UI.updateJellyfinUsersUI(data);
        }
    } catch (error) {
        console.error('Failed to fetch users:', error);
    }
};

// Refresh playlists
window.refreshPlaylists = async function() {
    try {
        showToast('Refreshing playlists...', 'success');
        const data = await API.refreshPlaylists();
        showToast(data.message, 'success');
        setTimeout(window.fetchPlaylists, 2000);
    } catch (error) {
        showToast('Failed to refresh playlists', 'error');
    }
};

// Clear playlist cache
window.clearPlaylistCache = async function(name) {
    if (!confirm(`Rebuild "${name}" from scratch?\n\nThis will:\n• Fetch fresh Spotify playlist data\n• Clear all caches\n• Re-match all tracks\n\nUse this when the Spotify playlist has changed.\n\nThis may take a minute.`)) return;
    
    try {
        document.getElementById('matching-warning-banner').style.display = 'block';
        showToast(`Rebuilding ${name} from scratch...`, 'info');
        const data = await API.clearPlaylistCache(name);
        showToast(`✓ ${data.message}`, 'success', 5000);
        UI.showPlaylistRebuildingIndicator(name);
        setTimeout(() => {
            window.fetchPlaylists();
            document.getElementById('matching-warning-banner').style.display = 'none';
        }, 3000);
    } catch (error) {
        showToast('Failed to clear cache', 'error');
        document.getElementById('matching-warning-banner').style.display = 'none';
    }
};

// Match playlist tracks
window.matchPlaylistTracks = async function(name) {
    try {
        document.getElementById('matching-warning-banner').style.display = 'block';
        showToast(`Re-matching local tracks for ${name}...`, 'info');
        const data = await API.matchPlaylistTracks(name);
        showToast(`✓ ${data.message}`, 'success');
        setTimeout(() => {
            window.fetchPlaylists();
            document.getElementById('matching-warning-banner').style.display = 'none';
        }, 2000);
    } catch (error) {
        showToast('Failed to re-match tracks', 'error');
        document.getElementById('matching-warning-banner').style.display = 'none';
    }
};

// Match all playlists
window.matchAllPlaylists = async function() {
    if (!confirm('Re-match local tracks for ALL playlists?\n\nUse this when your local library has changed.\n\nThis may take a few minutes.')) return;
    
    try {
        document.getElementById('matching-warning-banner').style.display = 'block';
        showToast('Matching tracks for all playlists...', 'success');
        const data = await API.matchAllPlaylists();
        showToast(`✓ ${data.message}`, 'success');
        setTimeout(() => {
            window.fetchPlaylists();
            document.getElementById('matching-warning-banner').style.display = 'none';
        }, 2000);
    } catch (error) {
        showToast('Failed to match tracks', 'error');
        document.getElementById('matching-warning-banner').style.display = 'none';
    }
};

// Refresh and match all
window.refreshAndMatchAll = async function() {
    if (!confirm('Clear caches, refresh from Spotify, and match all tracks?\n\nThis will:\n• Clear all playlist caches\n• Fetch fresh data from Spotify\n• Match all tracks against local library and external providers\n\nThis may take several minutes.')) return;
    
    try {
        document.getElementById('matching-warning-banner').style.display = 'block';
        showToast('Starting full refresh and match...', 'info', 3000);
        
        showToast('Step 1/3: Clearing caches...', 'info', 2000);
        await API.clearCache();
        await new Promise(resolve => setTimeout(resolve, 2000));
        
        showToast('Step 2/3: Fetching from Spotify...', 'info', 2000);
        await API.refreshPlaylists();
        await new Promise(resolve => setTimeout(resolve, 5000));
        
        showToast('Step 3/3: Matching all tracks (this may take several minutes)...', 'info', 3000);
        const data = await API.matchAllPlaylists();
        showToast(`✓ Full refresh and match complete!`, 'success', 5000);
        
        setTimeout(() => {
            window.fetchPlaylists();
            document.getElementById('matching-warning-banner').style.display = 'none';
        }, 3000);
    } catch (error) {
        showToast('Failed to complete refresh and match', 'error');
        document.getElementById('matching-warning-banner').style.display = 'none';
    }
};

// Clear cache
window.clearCache = async function() {
    if (!confirm('Clear all cached playlist data?')) return;
    
    try {
        const data = await API.clearCache();
        showToast(data.message, 'success');
        window.fetchPlaylists();
    } catch (error) {
        showToast('Failed to clear cache', 'error');
    }
};

// Export/Import env
window.exportEnv = async function() {
    try {
        const blob = await API.exportEnv();
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `.env.backup.${new Date().toISOString().split('T')[0]}`;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(url);
        document.body.removeChild(a);
        showToast('.env file exported successfully', 'success');
    } catch (error) {
        showToast('Failed to export .env file', 'error');
    }
};

window.importEnv = async function(event) {
    const file = event.target.files[0];
    if (!file) return;
    
    if (!confirm('Import this .env file? This will replace your current configuration.\n\nA backup will be created automatically.\n\nYou will need to restart the container for changes to take effect.')) {
        event.target.value = '';
        return;
    }
    
    try {
        const data = await API.importEnv(file);
        showToast(data.message, 'success');
    } catch (error) {
        showToast(error.message || 'Failed to import .env file', 'error');
    }
    
    event.target.value = '';
};

// Restart container
window.restartContainer = async function() {
    if (!confirm('Restart the container to apply configuration changes?\n\nThe dashboard will be temporarily unavailable.')) {
        return;
    }
    
    try {
        await API.restartContainer();
        document.getElementById('restart-overlay').classList.add('active');
        document.getElementById('restart-status').textContent = 'Stopping container...';
        
        setTimeout(() => {
            document.getElementById('restart-status').textContent = 'Waiting for server to come back...';
            checkServerAndReload();
        }, 3000);
    } catch (error) {
        showToast('Failed to restart container', 'error');
    }
};

async function checkServerAndReload() {
    let attempts = 0;
    const maxAttempts = 60;
    
    const checkHealth = async () => {
        try {
            const res = await fetch('/api/admin/status', {
                method: 'GET',
                cache: 'no-store'
            });
            if (res.ok) {
                document.getElementById('restart-status').textContent = 'Server is back! Reloading...';
                window.dismissRestartBanner();
                setTimeout(() => window.location.reload(), 500);
                return;
            }
        } catch (e) {
            // Server still restarting
        }
        
        attempts++;
        document.getElementById('restart-status').textContent = `Waiting for server to come back... (${attempts}s)`;
        
        if (attempts < maxAttempts) {
            setTimeout(checkHealth, 1000);
        } else {
            document.getElementById('restart-overlay').classList.remove('active');
            showToast('Server may still be restarting. Please refresh manually.', 'warning');
        }
    };
    
    checkHealth();
}

// Link mode switching
window.switchLinkMode = function(mode) {
    currentLinkMode = mode;
    
    const selectGroup = document.getElementById('link-select-group');
    const manualGroup = document.getElementById('link-manual-group');
    const selectBtn = document.getElementById('select-mode-btn');
    const manualBtn = document.getElementById('manual-mode-btn');
    
    if (mode === 'select') {
        selectGroup.style.display = 'block';
        manualGroup.style.display = 'none';
        selectBtn.classList.add('primary');
        manualBtn.classList.remove('primary');
    } else {
        selectGroup.style.display = 'none';
        manualGroup.style.display = 'block';
        selectBtn.classList.remove('primary');
        manualBtn.classList.add('primary');
    }
};

// Open link playlist modal
window.openLinkPlaylist = async function(jellyfinId, name) {
    document.getElementById('link-jellyfin-id').value = jellyfinId;
    document.getElementById('link-jellyfin-name').value = name;
    document.getElementById('link-spotify-id').value = '';
    
    window.switchLinkMode('select');
    
    if (spotifyUserPlaylists.length === 0) {
        const select = document.getElementById('link-spotify-select');
        select.innerHTML = '<option value="">Loading playlists...</option>';
        
        try {
            spotifyUserPlaylists = await API.fetchSpotifyUserPlaylists();
            const availablePlaylists = spotifyUserPlaylists.filter(p => !p.isLinked);
            
            if (availablePlaylists.length === 0) {
                select.innerHTML = '<option value="">No playlists available</option>';
                window.switchLinkMode('manual');
            } else {
                select.innerHTML = '<option value="">-- Select a playlist --</option>' +
                    availablePlaylists.map(p =>
                        `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)} (${p.trackCount} tracks)</option>`
                    ).join('');
            }
        } catch (error) {
            select.innerHTML = '<option value="">Failed to load playlists</option>';
            window.switchLinkMode('manual');
        }
    }
    
    openModal('link-playlist-modal');
};

// Link playlist
window.linkPlaylist = async function() {
    const jellyfinId = document.getElementById('link-jellyfin-id').value;
    const name = document.getElementById('link-jellyfin-name').value;
    const syncSchedule = document.getElementById('link-sync-schedule').value.trim();
    
    if (!syncSchedule) {
        showToast('Sync schedule is required', 'error');
        return;
    }
    
    const cronParts = syncSchedule.split(/\s+/);
    if (cronParts.length !== 5) {
        showToast('Invalid cron format. Expected: minute hour day month dayofweek', 'error');
        return;
    }
    
    let spotifyId = '';
    if (currentLinkMode === 'select') {
        spotifyId = document.getElementById('link-spotify-select').value;
        if (!spotifyId) {
            showToast('Please select a Spotify playlist', 'error');
            return;
        }
    } else {
        spotifyId = document.getElementById('link-spotify-id').value.trim();
        if (!spotifyId) {
            showToast('Spotify Playlist ID is required', 'error');
            return;
        }
    }
    
    // Clean Spotify ID
    let cleanSpotifyId = spotifyId;
    if (spotifyId.startsWith('spotify:playlist:')) {
        cleanSpotifyId = spotifyId.replace('spotify:playlist:', '');
    } else if (spotifyId.includes('spotify.com/playlist/')) {
        const match = spotifyId.match(/playlist\/([a-zA-Z0-9]+)/);
        if (match) cleanSpotifyId = match[1];
    }
    cleanSpotifyId = cleanSpotifyId.split('?')[0].split('#')[0].replace(/\/$/, '');
    
    try {
        await API.linkPlaylist(jellyfinId, cleanSpotifyId, syncSchedule);
        showToast('Playlist linked!', 'success');
        window.showRestartBanner();
        closeModal('link-playlist-modal');
        spotifyUserPlaylists = [];
        window.fetchPlaylists();
    } catch (error) {
        showToast(error.message || 'Failed to link playlist', 'error');
    }
};

// Unlink playlist
window.unlinkPlaylist = async function(name) {
    if (!confirm(`Unlink playlist "${name}"? This will stop filling in missing tracks.`)) return;
    
    try {
        await API.unlinkPlaylist(name);
        showToast('Playlist unlinked.', 'success');
        window.showRestartBanner();
        spotifyUserPlaylists = [];
        window.fetchPlaylists();
    } catch (error) {
        showToast(error.message || 'Failed to unlink playlist', 'error');
    }
};

// Add playlist
window.openAddPlaylist = function() {
    document.getElementById('new-playlist-name').value = '';
    document.getElementById('new-playlist-id').value = '';
    openModal('add-playlist-modal');
};

window.addPlaylist = async function() {
    const name = document.getElementById('new-playlist-name').value.trim();
    const id = document.getElementById('new-playlist-id').value.trim();
    
    if (!name || !id) {
        showToast('Name and ID are required', 'error');
        return;
    }
    
    try {
        await API.addPlaylist(name, id);
        showToast('Playlist added.', 'success');
        window.showRestartBanner();
        closeModal('add-playlist-modal');
    } catch (error) {
        showToast(error.message || 'Failed to add playlist', 'error');
    }
};

// Edit playlist schedule
window.editPlaylistSchedule = async function(playlistName, currentSchedule) {
    const newSchedule = prompt(`Edit sync schedule for "${playlistName}"\n\nCron format: minute hour day month dayofweek\nExamples:\n• 0 8 * * * = Daily 8 AM\n• 0 8 * * 1 = Monday 8 AM\n• 0 6 * * * = Daily 6 AM\n• 0 20 * * 5 = Friday 8 PM\n\nUse https://crontab.guru/ to build your schedule`, currentSchedule);
    
    if (!newSchedule || newSchedule === currentSchedule) return;
    
    const cronParts = newSchedule.trim().split(/\s+/);
    if (cronParts.length !== 5) {
        showToast('Invalid cron format. Expected: minute hour day month dayofweek', 'error');
        return;
    }
    
    try {
        await API.editPlaylistSchedule(playlistName, newSchedule.trim());
        showToast('Sync schedule updated!', 'success');
        window.showRestartBanner();
        window.fetchPlaylists();
    } catch (error) {
        console.error('Failed to update schedule:', error);
        showToast(error.message || 'Failed to update schedule', 'error');
    }
};

// Remove playlist
window.removePlaylist = async function(name) {
    if (!confirm(`Remove playlist "${name}"?`)) return;
    
    try {
        await API.removePlaylist(name);
        showToast('Playlist removed.', 'success');
        window.showRestartBanner();
        window.fetchPlaylists();
    } catch (error) {
        showToast(error.message || 'Failed to remove playlist', 'error');
    }
};

// View tracks
window.viewTracks = viewTracks;

// Manual mapping functions
window.openManualMap = openManualMap;
window.openExternalMap = openExternalMap;
window.openMapToLocal = openManualMap; // Alias for compatibility
window.openMapToExternal = openExternalMap; // Alias for compatibility
window.searchJellyfinTracks = searchJellyfinTracks;
window.selectJellyfinTrack = selectJellyfinTrack;
window.saveLocalMapping = saveLocalMapping;
window.saveManualMapping = saveManualMapping;
window.extractJellyfinId = extractJellyfinId;
window.validateExternalMapping = validateExternalMapping;

// Lyrics mapping
window.openLyricsMap = openLyricsMap;
window.saveLyricsMapping = saveLyricsMapping;

// Search provider
window.searchProvider = searchProvider;

// Settings editing
window.openEditSetting = function(envKey, label, inputType, helpText = '', options = []) {
    currentEditKey = envKey;
    currentEditType = inputType;
    currentEditOptions = options;
    
    document.getElementById('edit-setting-title').textContent = 'Edit ' + label;
    document.getElementById('edit-setting-label').textContent = label;
    
    const helpEl = document.getElementById('edit-setting-help');
    if (helpText) {
        helpEl.textContent = helpText;
        helpEl.style.display = 'block';
    } else {
        helpEl.style.display = 'none';
    }
    
    const container = document.getElementById('edit-setting-input-container');
    
    if (inputType === 'toggle') {
        container.innerHTML = `
            <select id="edit-setting-value">
                <option value="true">Enabled</option>
                <option value="false">Disabled</option>
            </select>
        `;
    } else if (inputType === 'select') {
        container.innerHTML = `
            <select id="edit-setting-value">
                ${options.map(opt => `<option value="${opt}">${opt}</option>`).join('')}
            </select>
        `;
    } else if (inputType === 'password') {
        container.innerHTML = `<input type="password" id="edit-setting-value" placeholder="Enter new value" autocomplete="off">`;
    } else if (inputType === 'number') {
        container.innerHTML = `<input type="number" id="edit-setting-value" placeholder="Enter value">`;
    } else {
        container.innerHTML = `<input type="text" id="edit-setting-value" placeholder="Enter value">`;
    }
    
    openModal('edit-setting-modal');
};

window.openEditCacheSetting = function(settingKey, label, helpText) {
    currentEditKey = settingKey;
    currentEditType = 'number';
    
    document.getElementById('edit-setting-title').textContent = 'Edit ' + label;
    document.getElementById('edit-setting-label').textContent = label;
    
    const helpEl = document.getElementById('edit-setting-help');
    if (helpText) {
        helpEl.textContent = helpText + ' (Requires restart to apply)';
        helpEl.style.display = 'block';
    } else {
        helpEl.style.display = 'none';
    }
    
    const container = document.getElementById('edit-setting-input-container');
    container.innerHTML = `<input type="number" id="edit-setting-value" placeholder="Enter value" min="1">`;
    
    openModal('edit-setting-modal');
};

window.saveEditSetting = async function() {
    const value = document.getElementById('edit-setting-value').value.trim();
    
    if (!value && currentEditType !== 'toggle') {
        showToast('Value is required', 'error');
        return;
    }
    
    try {
        await API.updateConfigSetting(currentEditKey, value);
        showToast('Setting updated.', 'success');
        window.showRestartBanner();
        closeModal('edit-setting-modal');
        window.fetchConfig();
        window.fetchStatus();
    } catch (error) {
        showToast(error.message || 'Failed to update setting', 'error');
    }
};

// Endpoint usage
window.fetchEndpointUsage = async function() {
    try {
        const topSelect = document.getElementById('endpoints-top-select');
        const top = topSelect ? topSelect.value : 50;
        const data = await API.fetchEndpointUsage(top);
        UI.updateEndpointUsageUI(data);
    } catch (error) {
        console.error('Failed to fetch endpoint usage:', error);
        const tbody = document.getElementById('endpoints-table-body');
        tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;color:var(--error);padding:40px;">Failed to load endpoint usage data</td></tr>';
    }
};

window.clearEndpointUsage = async function() {
    if (!confirm('Are you sure you want to clear all endpoint usage data? This cannot be undone.')) {
        return;
    }
    
    try {
        const data = await API.clearEndpointUsage();
        showToast(data.message || 'Endpoint usage data cleared', 'success');
        window.fetchEndpointUsage();
    } catch (error) {
        console.error('Failed to clear endpoint usage:', error);
        showToast('Failed to clear endpoint usage data', 'error');
    }
};

// Auto-refresh functionality
function startPlaylistAutoRefresh() {
    if (playlistAutoRefreshInterval) {
        clearInterval(playlistAutoRefreshInterval);
    }
    
    playlistAutoRefreshInterval = setInterval(() => {
        const playlistsTab = document.getElementById('tab-playlists');
        if (playlistsTab && playlistsTab.classList.contains('active')) {
            window.fetchPlaylists(true);
        }
    }, 5000);
}

function stopPlaylistAutoRefresh() {
    if (playlistAutoRefreshInterval) {
        clearInterval(playlistAutoRefreshInterval);
        playlistAutoRefreshInterval = null;
    }
}

// Initialize on load
document.addEventListener('DOMContentLoaded', () => {
    console.log('🚀 Allstarr Admin UI (Modular) loaded');
    
    // Setup tab switching
    document.querySelectorAll('.tab').forEach(tab => {
        tab.addEventListener('click', () => {
            window.switchTab(tab.dataset.tab);
        });
    });
    
    // Restore tab from URL hash
    const hash = window.location.hash.substring(1);
    if (hash) {
        window.switchTab(hash);
    }
    
    // Setup modal backdrop close
    setupModalBackdropClose();
    
    // Initial data load
    window.fetchStatus();
    window.fetchPlaylists();
    window.fetchTrackMappings();
    window.fetchMissingTracks();
    window.fetchDownloads();
    window.fetchJellyfinUsers();
    window.fetchJellyfinPlaylists();
    window.fetchConfig();
    window.fetchEndpointUsage();
    
    // Start auto-refresh
    startPlaylistAutoRefresh();
    
    // Load scrobbling config immediately on page load
    loadScrobblingConfig();
    
    // Also reload when scrobbling tab is clicked
    const scrobblingTab = document.querySelector('.tab[data-tab="scrobbling"]');
    if (scrobblingTab) {
        scrobblingTab.addEventListener('click', function() {
            loadScrobblingConfig();
        });
    }
    
    // Auto-refresh every 30 seconds
    setInterval(() => {
        window.fetchStatus();
        window.fetchPlaylists();
        window.fetchTrackMappings();
        window.fetchMissingTracks();
        window.fetchDownloads();
        
        const endpointsTab = document.getElementById('tab-endpoints');
        if (endpointsTab && endpointsTab.classList.contains('active')) {
            window.fetchEndpointUsage();
        }
    }, 30000);
});

console.log('✅ Main.js module loaded');

// ===== SCROBBLING FUNCTIONS =====

window.loadScrobblingConfig = async function() {
    try {
        const response = await fetch('/api/admin/config', {
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        
        const data = await response.json();
        
        // Update scrobbling enabled
        document.getElementById('scrobbling-enabled-value').textContent = data.scrobbling.enabled ? 'Enabled' : 'Disabled';
        
        // Update local tracks enabled
        document.getElementById('local-tracks-enabled-value').textContent = data.scrobbling.localTracksEnabled ? 'Enabled' : 'Disabled';
        
        // Update Last.fm config
        document.getElementById('lastfm-enabled-value').textContent = data.scrobbling.lastFm.enabled ? 'Enabled' : 'Disabled';
        
        // Username - show actual value or "Not Set"
        const username = data.scrobbling.lastFm.username;
        document.getElementById('lastfm-username-value').textContent = (username && username !== '(not set)') ? username : 'Not Set';
        
        // Password - show if set (masked)
        const password = data.scrobbling.lastFm.password;
        document.getElementById('lastfm-password-value').textContent = (password && password !== '(not set)') ? '••••••••' : 'Not Set';
        
        // Session key - show first 32 chars if exists
        const sessionKey = data.scrobbling.lastFm.sessionKey;
        if (sessionKey && sessionKey !== '(not set)' && !sessionKey.startsWith('••••')) {
            document.getElementById('lastfm-session-key-value').textContent = sessionKey.substring(0, 32) + '...';
        } else if (sessionKey && sessionKey.startsWith('••••')) {
            // It's masked, show it as is
            document.getElementById('lastfm-session-key-value').textContent = sessionKey;
        } else {
            document.getElementById('lastfm-session-key-value').textContent = 'Not Set';
        }
        
        // Status - check if API Key and Secret are set
        const hasApiKey = data.scrobbling.lastFm.apiKey && data.scrobbling.lastFm.apiKey !== '(not set)' && !data.scrobbling.lastFm.apiKey.startsWith('(not set)');
        const hasSecret = data.scrobbling.lastFm.sharedSecret && data.scrobbling.lastFm.sharedSecret !== '(not set)' && !data.scrobbling.lastFm.sharedSecret.startsWith('(not set)');
        const hasUsername = username && username !== '(not set)';
        const hasPassword = password && password !== '(not set)';
        const hasSessionKey = sessionKey && sessionKey !== '(not set)' && sessionKey.length > 0;
        
        let status = '';
        if (data.scrobbling.lastFm.enabled && hasSessionKey) {
            status = '<span style="color: var(--success);">✓ Configured & Enabled</span>';
        } else if (hasApiKey && hasSecret && hasUsername && hasPassword && !hasSessionKey) {
            status = '<span style="color: var(--warning);">⚠️ Ready to Authenticate</span>';
        } else if (hasApiKey && hasSecret && (!hasUsername || !hasPassword)) {
            status = '<span style="color: var(--warning);">⚠️ Needs Username & Password</span>';
        } else if (!hasApiKey || !hasSecret) {
            status = '<span style="color: var(--success);">✓ Using hardcoded credentials</span>';
        } else {
            status = '<span style="color: var(--muted);">○ Not Configured</span>';
        }
        document.getElementById('lastfm-status-value').innerHTML = status;
        
        // Update ListenBrainz config
        document.getElementById('listenbrainz-enabled-value').textContent = data.scrobbling.listenBrainz.enabled ? 'Enabled' : 'Disabled';
        
        const hasToken = data.scrobbling.listenBrainz.userToken && data.scrobbling.listenBrainz.userToken !== '(not set)';
        document.getElementById('listenbrainz-token-value').textContent = hasToken ? '••••••••' : 'Not Set';
        
        // ListenBrainz status
        let lbStatus = '';
        if (data.scrobbling.listenBrainz.enabled && hasToken) {
            lbStatus = '<span style="color: var(--success);">✓ Configured & Enabled</span>';
        } else if (hasToken && !data.scrobbling.listenBrainz.enabled) {
            lbStatus = '<span style="color: var(--warning);">⚠️ Token Set (Not Enabled)</span>';
        } else if (!hasToken && data.scrobbling.listenBrainz.enabled) {
            lbStatus = '<span style="color: var(--warning);">⚠️ Enabled (No Token)</span>';
        } else {
            lbStatus = '<span style="color: var(--muted);">○ Not Configured</span>';
        }
        document.getElementById('listenbrainz-status-value').innerHTML = lbStatus;
        
    } catch (error) {
        console.error('Failed to load scrobbling config:', error);
        showToast('Failed to load scrobbling configuration: ' + error.message, 'error');
    }
};

window.toggleScrobblingEnabled = async function() {
    try {
        const response = await fetch('/api/admin/config', {
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        const data = await response.json();
        const newValue = !data.scrobbling.enabled;
        
        await API.updateConfigSetting('SCROBBLING_ENABLED', newValue.toString());
        showToast(`Scrobbling ${newValue ? 'enabled' : 'disabled'}`, 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to toggle scrobbling: ' + error.message, 'error');
    }
};

window.toggleLocalTracksEnabled = async function() {
    try {
        const response = await fetch('/api/admin/scrobbling/status', {
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        const data = await response.json();
        const newValue = !data.localTracksEnabled;
        
        const updateResponse = await fetch('/api/admin/scrobbling/local-tracks/update', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-API-Key': localStorage.getItem('apiKey') || ''
            },
            body: JSON.stringify({ enabled: newValue })
        });
        
        if (!updateResponse.ok) {
            const error = await updateResponse.json();
            throw new Error(error.error || 'Failed to update setting');
        }
        
        const result = await updateResponse.json();
        showToast(result.message || `Local track scrobbling ${newValue ? 'enabled' : 'disabled'}`, 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to toggle local track scrobbling: ' + error.message, 'error');
    }
};

window.toggleLastFmEnabled = async function() {
    try {
        const response = await fetch('/api/admin/config', {
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        const data = await response.json();
        const newValue = !data.scrobbling.lastFm.enabled;
        
        await API.updateConfigSetting('SCROBBLING_LASTFM_ENABLED', newValue.toString());
        showToast(`Last.fm ${newValue ? 'enabled' : 'disabled'}`, 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to toggle Last.fm: ' + error.message, 'error');
    }
};

window.toggleListenBrainzEnabled = async function() {
    try {
        const response = await fetch('/api/admin/config', {
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        const data = await response.json();
        const newValue = !data.scrobbling.listenBrainz.enabled;
        
        await API.updateConfigSetting('SCROBBLING_LISTENBRAINZ_ENABLED', newValue.toString());
        showToast(`ListenBrainz ${newValue ? 'enabled' : 'disabled'}`, 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to toggle ListenBrainz: ' + error.message, 'error');
    }
};

window.editLastFmUsername = async function() {
    const value = prompt('Enter your Last.fm username:');
    if (value === null) return;
    
    try {
        await API.updateConfigSetting('SCROBBLING_LASTFM_USERNAME', value.trim());
        showToast('Last.fm username updated', 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to update username: ' + error.message, 'error');
    }
};

window.editLastFmPassword = async function() {
    const value = prompt('Enter your Last.fm password:\n\nThis is stored encrypted and only used for authentication.');
    if (value === null) return;
    
    try {
        await API.updateConfigSetting('SCROBBLING_LASTFM_PASSWORD', value.trim());
        showToast('Last.fm password updated', 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to update password: ' + error.message, 'error');
    }
};

window.editListenBrainzToken = async function() {
    const value = prompt('Enter your ListenBrainz User Token:\n\nGet from https://listenbrainz.org/profile/');
    if (value === null) return;
    
    try {
        await API.updateConfigSetting('SCROBBLING_LISTENBRAINZ_USER_TOKEN', value.trim());
        showToast('ListenBrainz token updated', 'success');
        await loadScrobblingConfig();
    } catch (error) {
        showToast('Failed to update token: ' + error.message, 'error');
    }
};

window.authenticateLastFm = async function() {
    try {
        showToast('Authenticating with Last.fm...', 'info');
        
        const response = await fetch('/api/admin/scrobbling/lastfm/authenticate', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-API-Key': localStorage.getItem('apiKey') || ''
            }
        });
        
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || `HTTP ${response.status}`);
        }
        
        const data = await response.json();
        
        showToast('✓ Authentication successful! Session key saved. Please restart the container.', 'success', 5000);
        window.showRestartBanner();
        
        // Reload config to show updated session key
        await loadScrobblingConfig();
    } catch (error) {
        console.error('Failed to authenticate:', error);
        showToast('Authentication failed: ' + error.message, 'error');
    }
};

window.testLastFmConnection = async function() {
    try {
        const response = await fetch('/api/admin/scrobbling/lastfm/test', {
            method: 'POST',
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || `HTTP ${response.status}`);
        }
        
        const data = await response.json();
        
        showToast(`✓ Last.fm connection successful! User: ${data.username}, Scrobbles: ${data.playcount}`, 'success');
    } catch (error) {
        console.error('Failed to test connection:', error);
        showToast('Failed to test connection: ' + error.message, 'error');
    }
};

window.validateListenBrainzToken = async function() {
    const token = prompt('Enter your ListenBrainz User Token:\n\nGet from https://listenbrainz.org/settings/');
    if (!token) return;
    
    try {
        showToast('Validating ListenBrainz token...', 'info');
        
        const response = await fetch('/api/admin/scrobbling/listenbrainz/validate', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-API-Key': localStorage.getItem('apiKey') || ''
            },
            body: JSON.stringify({ userToken: token.trim() })
        });
        
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || `HTTP ${response.status}`);
        }
        
        const data = await response.json();
        
        showToast(`✓ Token validated! User: ${data.username}. Please restart the container.`, 'success', 5000);
        window.showRestartBanner();
        
        // Reload config to show updated token
        await loadScrobblingConfig();
    } catch (error) {
        console.error('Failed to validate token:', error);
        showToast('Validation failed: ' + error.message, 'error');
    }
};

window.testListenBrainzConnection = async function() {
    try {
        const response = await fetch('/api/admin/scrobbling/listenbrainz/test', {
            method: 'POST',
            headers: { 'X-API-Key': localStorage.getItem('apiKey') || '' }
        });
        
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || `HTTP ${response.status}`);
        }
        
        const data = await response.json();
        
        showToast(`✓ ListenBrainz connection successful! User: ${data.username}`, 'success');
    } catch (error) {
        console.error('Failed to test connection:', error);
        showToast('Failed to test connection: ' + error.message, 'error');
    }
};
