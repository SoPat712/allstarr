// Helper functions for complex UI operations

import { escapeHtml, escapeJs, showToast, capitalizeProvider } from './utils.js';
import * as API from './api.js';
import { openModal, closeModal } from './modals.js';

let searchTimeout = null;

// View tracks modal
export async function viewTracks(name) {
    document.getElementById('tracks-modal-title').textContent = name + ' - Tracks';
    document.getElementById('tracks-list').innerHTML = '<div class="loading"><span class="spinner"></span> Loading tracks...</div>';
    openModal('tracks-modal');
    
    try {
        const data = await API.fetchPlaylistTracks(name);
        
        if (!data || !data.tracks) {
            document.getElementById('tracks-list').innerHTML = '<p style="text-align:center;color:var(--error);padding:40px;">Invalid data received from server</p>';
            return;
        }
        
        if (data.tracks.length === 0) {
            document.getElementById('tracks-list').innerHTML = '<p style="text-align:center;color:var(--text-secondary);padding:40px;">No tracks found</p>';
            return;
        }
        
        document.getElementById('tracks-list').innerHTML = data.tracks.map((t, index) => {
            let statusBadge = '';
            let mapButton = '';
            let lyricsBadge = '';
            
            if (t.hasLyrics) {
                lyricsBadge = '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:#3b82f6;color:white;"><span class="status-dot" style="background:white;"></span>Lyrics</span>';
            }
            
            if (t.isLocal === true) {
                statusBadge = '<span class="status-badge success" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;"><span class="status-dot"></span>Local</span>';
                if (t.isManualMapping && t.manualMappingType === 'jellyfin') {
                    statusBadge += '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:var(--info);color:white;"><span class="status-dot" style="background:white;"></span>Manual</span>';
                }
            } else if (t.isLocal === false) {
                const provider = capitalizeProvider(t.externalProvider) || 'External';
                statusBadge = `<span class="status-badge info" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;"><span class="status-dot"></span>${escapeHtml(provider)}</span>`;
                if (t.isManualMapping && t.manualMappingType === 'external') {
                    statusBadge += '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:var(--info);color:white;"><span class="status-dot" style="background:white;"></span>Manual</span>';
                }
                const firstArtist = (t.artists && t.artists.length > 0) ? t.artists[0] : '';
                mapButton = `<button class="small map-track-btn" 
                            data-playlist-name="${escapeHtml(name)}" 
                            data-position="${t.position}" 
                            data-title="${escapeHtml(t.title || '')}" 
                            data-artist="${escapeHtml(firstArtist)}" 
                            data-spotify-id="${escapeHtml(t.spotifyId || '')}" 
                            style="margin-left:8px;font-size:0.75rem;padding:4px 8px;">Map to Local</button>
                            <button class="small map-external-btn" 
                            data-playlist-name="${escapeHtml(name)}" 
                            data-position="${t.position}" 
                            data-title="${escapeHtml(t.title || '')}" 
                            data-artist="${escapeHtml(firstArtist)}" 
                            data-spotify-id="${escapeHtml(t.spotifyId || '')}" 
                            style="margin-left:4px;font-size:0.75rem;padding:4px 8px;background:var(--warning);border-color:var(--warning);">Map to External</button>`;
            } else {
                statusBadge = '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;background:rgba(245, 158, 11, 0.2);color:#f59e0b;"><span class="status-dot" style="background:#f59e0b;"></span>Missing</span>';
                const firstArtist = (t.artists && t.artists.length > 0) ? t.artists[0] : '';
                mapButton = `<button class="small map-track-btn" 
                            data-playlist-name="${escapeHtml(name)}" 
                            data-position="${t.position}" 
                            data-title="${escapeHtml(t.title || '')}" 
                            data-artist="${escapeHtml(firstArtist)}" 
                            data-spotify-id="${escapeHtml(t.spotifyId || '')}" 
                            style="margin-left:8px;font-size:0.75rem;padding:4px 8px;">Map to Local</button>
                            <button class="small map-external-btn" 
                            data-playlist-name="${escapeHtml(name)}" 
                            data-position="${t.position}" 
                            data-title="${escapeHtml(t.title || '')}" 
                            data-artist="${escapeHtml(firstArtist)}" 
                            data-spotify-id="${escapeHtml(t.spotifyId || '')}" 
                            style="margin-left:4px;font-size:0.75rem;padding:4px 8px;background:var(--warning);border-color:var(--warning);">Map to External</button>`;
            }
            
            const firstArtist = (t.artists && t.artists.length > 0) ? t.artists[0] : '';
            const searchLinkText = `${t.title} - ${firstArtist}`;
            const durationSeconds = Math.floor((t.durationMs || 0) / 1000);
            
            const lyricsMapButton = `<button class="small" onclick="openLyricsMap('${escapeJs(firstArtist)}', '${escapeJs(t.title)}', '${escapeJs(t.album || '')}', ${durationSeconds})" style="margin-left:4px;font-size:0.75rem;padding:4px 8px;background:#3b82f6;border-color:#3b82f6;color:white;">Map Lyrics ID</button>`;
            
            return `
                <div class="track-item" data-position="${t.position}">
                    <span class="track-position">${index + 1}</span>
                    <div class="track-info">
                        <h4>${escapeHtml(t.title)}${statusBadge}${lyricsBadge}${mapButton}${lyricsMapButton}</h4>
                        <span class="artists">${escapeHtml((t.artists || []).join(', '))}</span>
                    </div>
                    <div class="track-meta">
                        ${t.album ? escapeHtml(t.album) : ''}
                        ${t.isrc ? '<br><small>ISRC: ' + t.isrc + '</small>' : ''}
                        ${t.isLocal === false && t.searchQuery && t.externalProvider ? '<br><small style="color:var(--accent)"><a href="#" onclick="searchProvider(\'' + escapeJs(t.searchQuery) + '\', \'' + escapeJs(t.externalProvider) + '\'); return false;" style="color:var(--accent);text-decoration:underline;">🔍 Search: ' + escapeHtml(searchLinkText) + '</a></small>' : ''}
                        ${t.isLocal === null && t.searchQuery ? '<br><small style="color:var(--text-secondary)"><a href="#" onclick="searchProvider(\'' + escapeJs(t.searchQuery) + '\', \'squidwtf\'); return false;" style="color:var(--text-secondary);text-decoration:underline;">🔍 Search: ' + escapeHtml(searchLinkText) + '</a></small>' : ''}
                    </div>
                </div>
            `;
        }).join('');
        
        // Add event listeners
        document.querySelectorAll('.map-track-btn').forEach(btn => {
            btn.addEventListener('click', function() {
                const playlistName = this.getAttribute('data-playlist-name');
                const position = parseInt(this.getAttribute('data-position'));
                const title = this.getAttribute('data-title');
                const artist = this.getAttribute('data-artist');
                const spotifyId = this.getAttribute('data-spotify-id');
                openManualMap(playlistName, position, title, artist, spotifyId);
            });
        });
        
        document.querySelectorAll('.map-external-btn').forEach(btn => {
            btn.addEventListener('click', function() {
                const playlistName = this.getAttribute('data-playlist-name');
                const position = parseInt(this.getAttribute('data-position'));
                const title = this.getAttribute('data-title');
                const artist = this.getAttribute('data-artist');
                const spotifyId = this.getAttribute('data-spotify-id');
                openExternalMap(playlistName, position, title, artist, spotifyId);
            });
        });
    } catch (error) {
        console.error('Error in viewTracks:', error);
        document.getElementById('tracks-list').innerHTML = '<p style="text-align:center;color:var(--error);padding:40px;">Failed to load tracks: ' + error.message + '</p>';
    }
}

// Manual mapping to local Jellyfin track
export function openManualMap(playlistName, position, title, artist, spotifyId) {
    document.getElementById('manual-map-title').textContent = `${title} - ${artist}`;
    document.getElementById('manual-map-playlist').value = playlistName;
    document.getElementById('manual-map-position').value = position;
    document.getElementById('manual-map-spotify-id').value = spotifyId;
    document.getElementById('jellyfin-search-query').value = `${title} ${artist}`;
    document.getElementById('jellyfin-results').innerHTML = '<p style="color:var(--text-secondary);text-align:center;padding:20px;">Enter search terms and click Search</p>';
    openModal('manual-map-modal');
}

// Manual mapping to external provider
export function openExternalMap(playlistName, position, title, artist, spotifyId) {
    document.getElementById('external-map-title').textContent = `${title} - ${artist}`;
    document.getElementById('external-map-playlist').value = playlistName;
    document.getElementById('external-map-position').value = position;
    document.getElementById('external-map-spotify-id').value = spotifyId;
    document.getElementById('external-map-external-id').value = '';
    document.getElementById('external-map-provider').value = 'squidwtf';
    openModal('external-map-modal');
}

// Search Jellyfin for tracks
export async function searchJellyfinTracks() {
    const query = document.getElementById('jellyfin-search-query').value.trim();
    if (!query) {
        showToast('Please enter a search query', 'error');
        return;
    }
    
    const resultsDiv = document.getElementById('jellyfin-results');
    resultsDiv.innerHTML = '<div class="loading"><span class="spinner"></span> Searching...</div>';
    
    try {
        const data = await API.searchJellyfin(query);
        
        if (!data.results || data.results.length === 0) {
            resultsDiv.innerHTML = '<p style="color:var(--text-secondary);text-align:center;padding:20px;">No results found</p>';
            return;
        }
        
        resultsDiv.innerHTML = data.results.map(track => {
            return `
                <div class="jellyfin-result" onclick="selectJellyfinTrack('${escapeJs(track.id)}')">
                    <div>
                        <strong>${escapeHtml(track.name)}</strong>
                        <br>
                        <span style="color:var(--text-secondary);">${escapeHtml(track.artist || '')}</span>
                        ${track.album ? '<br><small>' + escapeHtml(track.album) + '</small>' : ''}
                    </div>
                    <div style="font-family:monospace;font-size:0.75rem;color:var(--text-secondary);">
                        ${track.id}
                    </div>
                </div>
            `;
        }).join('');
    } catch (error) {
        console.error('Search error:', error);
        resultsDiv.innerHTML = '<p style="color:var(--error);text-align:center;padding:20px;">Search failed: ' + error.message + '</p>';
    }
}

// Select a Jellyfin track from search results
export async function selectJellyfinTrack(jellyfinId) {
    try {
        const data = await API.getJellyfinTrack(jellyfinId);
        
        document.getElementById('manual-map-jellyfin-id').value = jellyfinId;
        document.getElementById('manual-map-preview').innerHTML = `
            <strong>Selected:</strong> ${escapeHtml(data.track.name)}<br>
            <span style="color:var(--text-secondary);">Artist: ${escapeHtml(data.track.artist || 'Unknown')}</span><br>
            ${data.track.album ? '<span style="color:var(--text-secondary);">Album: ' + escapeHtml(data.track.album) + '</span>' : ''}
        `;
        
        showToast('Track selected. Click "Save Mapping" to confirm.', 'success');
    } catch (error) {
        console.error('Failed to fetch track details:', error);
        showToast('Failed to fetch track details', 'error');
    }
}

// Save local (Jellyfin) mapping
export async function saveLocalMapping() {
    const playlistName = document.getElementById('manual-map-playlist').value;
    const position = parseInt(document.getElementById('manual-map-position').value);
    const spotifyId = document.getElementById('manual-map-spotify-id').value;
    const jellyfinId = document.getElementById('manual-map-jellyfin-id').value;
    
    if (!jellyfinId) {
        showToast('Please select a Jellyfin track first', 'error');
        return;
    }
    
    const saveBtn = document.getElementById('manual-map-save-btn');
    const originalText = saveBtn.textContent;
    saveBtn.textContent = 'Saving...';
    saveBtn.disabled = true;
    
    try {
        await API.saveTrackMapping(playlistName, {
            position,
            spotifyId,
            jellyfinId,
            type: 'jellyfin'
        });
        
        showToast('✓ Mapping saved successfully', 'success');
        closeModal('manual-map-modal');
        
        if (window.fetchPlaylists) window.fetchPlaylists();
        if (window.fetchTrackMappings) window.fetchTrackMappings();
    } catch (error) {
        showToast(error.message || 'Failed to save mapping', 'error');
    } finally {
        saveBtn.textContent = originalText;
        saveBtn.disabled = false;
    }
}

// Save external provider mapping
export async function saveManualMapping() {
    const playlistName = document.getElementById('external-map-playlist').value;
    const position = parseInt(document.getElementById('external-map-position').value);
    const spotifyId = document.getElementById('external-map-spotify-id').value;
    const externalId = document.getElementById('external-map-external-id').value.trim();
    const provider = document.getElementById('external-map-provider').value;
    
    if (!externalId) {
        showToast('Please enter an external track ID', 'error');
        return;
    }
    
    if (!validateExternalMapping(externalId, provider)) {
        return;
    }
    
    const saveBtn = document.getElementById('external-map-save-btn');
    const originalText = saveBtn.textContent;
    saveBtn.textContent = 'Saving...';
    saveBtn.disabled = true;
    
    try {
        await API.saveTrackMapping(playlistName, {
            position,
            spotifyId,
            externalId,
            externalProvider: provider,
            type: 'external'
        });
        
        showToast('✓ External mapping saved successfully', 'success');
        closeModal('external-map-modal');
        
        if (window.fetchPlaylists) window.fetchPlaylists();
        if (window.fetchTrackMappings) window.fetchTrackMappings();
    } catch (error) {
        showToast(error.message || 'Failed to save mapping', 'error');
    } finally {
        saveBtn.textContent = originalText;
        saveBtn.disabled = false;
    }
}

// Extract Jellyfin ID from URL or raw ID
export function extractJellyfinId() {
    const input = document.getElementById('manual-map-jellyfin-url').value.trim();
    if (!input) return;
    
    let jellyfinId = '';
    
    if (input.includes('/')) {
        const match = input.match(/[a-f0-9]{32}/i);
        if (match) {
            jellyfinId = match[0];
        }
    } else if (/^[a-f0-9]{32}$/i.test(input)) {
        jellyfinId = input;
    }
    
    if (jellyfinId) {
        document.getElementById('manual-map-jellyfin-id').value = jellyfinId;
        selectJellyfinTrack(jellyfinId);
    } else {
        showToast('Invalid Jellyfin ID or URL format', 'error');
    }
}

// Validate external mapping ID format
export function validateExternalMapping(externalId, provider) {
    if (provider === 'squidwtf') {
        if (!/^https?:\/\//.test(externalId)) {
            showToast('SquidWTF requires a full URL (e.g., https://squid.wtf/music/...)', 'error');
            return false;
        }
    } else if (provider === 'deezer') {
        if (!/^\d+$/.test(externalId) && !externalId.startsWith('http')) {
            showToast('Deezer ID should be numeric or a full URL', 'error');
            return false;
        }
    } else if (provider === 'qobuz') {
        if (!externalId.includes('/') && !/^\d+$/.test(externalId)) {
            showToast('Qobuz ID format appears invalid', 'error');
            return false;
        }
    }
    return true;
}

// Open lyrics mapping modal
export function openLyricsMap(artist, title, album, durationSeconds) {
    document.getElementById('lyrics-map-artist').textContent = artist;
    document.getElementById('lyrics-map-title').textContent = title;
    document.getElementById('lyrics-map-album').textContent = album || '(No album)';
    document.getElementById('lyrics-map-artist-value').value = artist;
    document.getElementById('lyrics-map-title-value').value = title;
    document.getElementById('lyrics-map-album-value').value = album || '';
    document.getElementById('lyrics-map-duration').value = durationSeconds;
    document.getElementById('lyrics-map-id').value = '';
    
    openModal('lyrics-map-modal');
}

// Save lyrics mapping
export async function saveLyricsMapping() {
    const artist = document.getElementById('lyrics-map-artist-value').value;
    const title = document.getElementById('lyrics-map-title-value').value;
    const album = document.getElementById('lyrics-map-album-value').value;
    const durationSeconds = parseInt(document.getElementById('lyrics-map-duration').value);
    const lyricsId = parseInt(document.getElementById('lyrics-map-id').value);
    
    if (!lyricsId || lyricsId <= 0) {
        showToast('Please enter a valid lyrics ID', 'error');
        return;
    }
    
    const saveBtn = document.getElementById('lyrics-map-save-btn');
    const originalText = saveBtn.textContent;
    saveBtn.textContent = 'Saving...';
    saveBtn.disabled = true;
    
    try {
        const data = await API.saveLyricsMapping(artist, title, album, durationSeconds, lyricsId);
        
        if (data.cached && data.lyrics) {
            showToast(`✓ Lyrics mapped and cached: ${data.lyrics.trackName} by ${data.lyrics.artistName}`, 'success', 5000);
        } else {
            showToast('✓ Lyrics mapping saved successfully', 'success');
        }
        closeModal('lyrics-map-modal');
    } catch (error) {
        showToast(error.message || 'Failed to save lyrics mapping', 'error');
    } finally {
        saveBtn.textContent = originalText;
        saveBtn.disabled = false;
    }
}

// Search provider (open in new tab)
export async function searchProvider(query, provider) {
    try {
        const data = await API.getSquidWTFBaseUrl();
        const baseUrl = data.squidWtfBaseUrl || 'https://squid.wtf';
        const searchUrl = `${baseUrl}/music/search?q=${encodeURIComponent(query)}`;
        window.open(searchUrl, '_blank');
    } catch (error) {
        console.error('Failed to get SquidWTF base URL:', error);
        window.open(`https://squid.wtf/music/search?q=${encodeURIComponent(query)}`, '_blank');
    }
}