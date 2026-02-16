// UI updates and DOM manipulation

import { escapeHtml, escapeJs, capitalizeProvider } from './utils.js';

export function updateStatusUI(data) {
    const versionEl = document.getElementById('version');
    if (versionEl) versionEl.textContent = 'v' + data.version;
    
    const backendTypeEl = document.getElementById('backend-type');
    if (backendTypeEl) backendTypeEl.textContent = data.backendType;
    
    const jellyfinUrlEl = document.getElementById('jellyfin-url');
    if (jellyfinUrlEl) jellyfinUrlEl.textContent = data.jellyfinUrl || '-';
    
    const playlistCountEl = document.getElementById('playlist-count');
    if (playlistCountEl) playlistCountEl.textContent = data.spotifyImport.playlistCount;
    
    const cacheDurationEl = document.getElementById('cache-duration');
    if (cacheDurationEl) cacheDurationEl.textContent = data.spotify.cacheDurationMinutes + ' min';
    
    const isrcMatchingEl = document.getElementById('isrc-matching');
    if (isrcMatchingEl) isrcMatchingEl.textContent = data.spotify.preferIsrcMatching ? 'Enabled' : 'Disabled';
    
    const spotifyUserEl = document.getElementById('spotify-user');
    if (spotifyUserEl) spotifyUserEl.textContent = data.spotify.user || '-';
    
    const statusBadge = document.getElementById('spotify-status');
    const authStatus = document.getElementById('spotify-auth-status');
    
    if (data.spotify.authStatus === 'configured') {
        if (statusBadge) {
            statusBadge.className = 'status-badge success';
            statusBadge.innerHTML = '<span class="status-dot"></span>Spotify Ready';
        }
        if (authStatus) {
            authStatus.textContent = 'Cookie Set';
            authStatus.className = 'stat-value success';
        }
    } else if (data.spotify.authStatus === 'missing_cookie') {
        if (statusBadge) {
            statusBadge.className = 'status-badge warning';
            statusBadge.innerHTML = '<span class="status-dot"></span>Cookie Missing';
        }
        if (authStatus) {
            authStatus.textContent = 'No Cookie';
            authStatus.className = 'stat-value warning';
        }
    } else {
        if (statusBadge) {
            statusBadge.className = 'status-badge';
            statusBadge.innerHTML = '<span class="status-dot"></span>Not Configured';
        }
        if (authStatus) {
            authStatus.textContent = 'Not Configured';
            authStatus.className = 'stat-value';
        }
    }
}

export function updatePlaylistsUI(data) {
    const tbody = document.getElementById('playlist-table-body');
    
    if (data.playlists.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align:center;color:var(--text-secondary);padding:40px;">No playlists configured. Link playlists from the Jellyfin Playlists tab.</td></tr>';
        return;
    }
    
    tbody.innerHTML = data.playlists.map(p => {
        const spotifyTotal = p.trackCount || 0;
        const localCount = p.localTracks || 0;
        const externalMatched = p.externalMatched || 0;
        const externalMissing = p.externalMissing || 0;
        const totalPlayable = p.totalPlayable || (localCount + externalMatched);
        
        let statsHtml = `<span class="track-count">${totalPlayable}/${spotifyTotal}</span>`;
        
        let breakdownParts = [];
        if (localCount > 0) {
            breakdownParts.push(`<span style="color:var(--success)">${localCount} Local</span>`);
        }
        if (externalMatched > 0) {
            breakdownParts.push(`<span style="color:var(--accent)">${externalMatched} External</span>`);
        }
        if (externalMissing > 0) {
            breakdownParts.push(`<span style="color:var(--warning)">${externalMissing} Missing</span>`);
        }
        
        const breakdown = breakdownParts.length > 0
            ? `<br><small style="color:var(--text-secondary)">${breakdownParts.join(' • ')}</small>`
            : '';
        
        const completionPct = spotifyTotal > 0 ? Math.round((totalPlayable / spotifyTotal) * 100) : 0;
        const localPct = spotifyTotal > 0 ? Math.round((localCount / spotifyTotal) * 100) : 0;
        const externalPct = spotifyTotal > 0 ? Math.round((externalMatched / spotifyTotal) * 100) : 0;
        const missingPct = spotifyTotal > 0 ? Math.round((externalMissing / spotifyTotal) * 100) : 0;
        const completionColor = completionPct === 100 ? 'var(--success)' : completionPct >= 80 ? 'var(--accent)' : 'var(--warning)';
        
        const syncSchedule = p.syncSchedule || '0 8 * * *';
        
        return `
            <tr>
                <td><strong>${escapeHtml(p.name)}</strong></td>
                <td style="font-family:monospace;font-size:0.85rem;color:var(--text-secondary);">${p.id || '-'}</td>
                <td style="font-family:monospace;font-size:0.85rem;">
                    ${escapeHtml(syncSchedule)}
                    <button onclick="editPlaylistSchedule('${escapeJs(p.name)}', '${escapeJs(syncSchedule)}')" style="margin-left:4px;font-size:0.75rem;padding:2px 6px;">Edit</button>
                </td>
                <td>${statsHtml}${breakdown}</td>
                <td>
                    <div style="display:flex;align-items:center;gap:8px;">
                        <div style="flex:1;background:var(--bg-tertiary);height:12px;border-radius:6px;overflow:hidden;display:flex;">
                            <div style="width:${localPct}%;height:100%;background:#10b981;transition:width 0.3s;" title="${localCount} local tracks"></div>
                            <div style="width:${externalPct}%;height:100%;background:#3b82f6;transition:width 0.3s;" title="${externalMatched} external tracks"></div>
                            <div style="width:${missingPct}%;height:100%;background:#f59e0b;transition:width 0.3s;" title="${externalMissing} missing tracks"></div>
                        </div>
                        <span style="font-size:0.85rem;color:${completionColor};font-weight:500;min-width:40px;">${completionPct}%</span>
                    </div>
                </td>
                <td class="cache-age">${p.cacheAge || '-'}</td>
                <td>
                    <button onclick="matchPlaylistTracks('${escapeJs(p.name)}')" title="Re-match when local library changed">Re-match Local</button>
                    <button onclick="clearPlaylistCache('${escapeJs(p.name)}')" title="Rebuild when Spotify playlist changed" style="background:var(--accent);border-color:var(--accent);">Rebuild Remote</button>
                    <button onclick="viewTracks('${escapeJs(p.name)}')">View</button>
                    <button class="danger" onclick="removePlaylist('${escapeJs(p.name)}')">Remove</button>
                </td>
            </tr>
        `;
    }).join('');
}

export function updateTrackMappingsUI(data) {
    document.getElementById('mappings-total').textContent = data.externalCount || 0;
    document.getElementById('mappings-external').textContent = data.externalCount || 0;
    
    const tbody = document.getElementById('mappings-table-body');
    
    if (data.mappings.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:var(--text-secondary);padding:40px;">No manual mappings found.</td></tr>';
        return;
    }
    
    const externalMappings = data.mappings.filter(m => m.type === 'external');
    
    if (externalMappings.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:var(--text-secondary);padding:40px;">No external mappings found.</td></tr>';
        return;
    }
    
    tbody.innerHTML = externalMappings.map(m => {
        const typeColor = 'var(--success)';
        const typeBadge = `<span style="display:inline-block;padding:2px 8px;border-radius:4px;font-size:0.8rem;background:${typeColor}20;color:${typeColor};font-weight:500;">external</span>`;
        const targetDisplay = `<span style="font-family:monospace;font-size:0.85rem;color:var(--success);">${m.externalProvider}/${m.externalId}</span>`;
        const createdDate = m.createdAt ? new Date(m.createdAt).toLocaleString() : '-';
        
        return `
            <tr>
                <td><strong>${escapeHtml(m.playlist)}</strong></td>
                <td style="font-family:monospace;font-size:0.85rem;color:var(--text-secondary);">${m.spotifyId}</td>
                <td>${typeBadge}</td>
                <td>${targetDisplay}</td>
                <td style="color:var(--text-secondary);font-size:0.85rem;">${createdDate}</td>
                <td>
                    <button class="danger delete-mapping-btn" style="padding:4px 12px;font-size:0.8rem;" data-playlist="${escapeHtml(m.playlist)}" data-spotify-id="${m.spotifyId}">Remove</button>
                </td>
            </tr>
        `;
    }).join('');
}

export function updateDownloadsUI(data) {
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
}

export function updateConfigUI(data) {
    document.getElementById('config-backend-type').textContent = data.backendType || 'Jellyfin';
    document.getElementById('config-music-service').textContent = data.musicService || 'SquidWTF';
    document.getElementById('config-storage-mode').textContent = data.library?.storageMode || 'Cache';
    document.getElementById('config-cache-duration-hours').textContent = data.library?.cacheDurationHours || '24';
    document.getElementById('config-download-mode').textContent = data.library?.downloadMode || 'Track';
    document.getElementById('config-explicit-filter').textContent = data.explicitFilter || 'All';
    document.getElementById('config-enable-external-playlists').textContent = data.enableExternalPlaylists ? 'Yes' : 'No';
    document.getElementById('config-playlists-directory').textContent = data.playlistsDirectory || '(not set)';
    document.getElementById('config-redis-enabled').textContent = data.redisEnabled ? 'Yes' : 'No';
    
    document.getElementById('config-spotify-enabled').textContent = data.spotifyApi.enabled ? 'Yes' : 'No';
    document.getElementById('config-spotify-cookie').textContent = data.spotifyApi.sessionCookie;
    document.getElementById('config-cache-duration').textContent = data.spotifyApi.cacheDurationMinutes + ' minutes';
    document.getElementById('config-isrc-matching').textContent = data.spotifyApi.preferIsrcMatching ? 'Enabled' : 'Disabled';
    
    document.getElementById('config-deezer-arl').textContent = data.deezer.arl || '(not set)';
    document.getElementById('config-deezer-quality').textContent = data.deezer.quality;
    document.getElementById('config-squid-quality').textContent = data.squidWtf.quality;
    document.getElementById('config-musicbrainz-enabled').textContent = data.musicBrainz.enabled ? 'Yes' : 'No';
    document.getElementById('config-qobuz-token').textContent = data.qobuz.userAuthToken || '(not set)';
    document.getElementById('config-qobuz-quality').textContent = data.qobuz.quality || 'FLAC';
    document.getElementById('config-jellyfin-url').textContent = data.jellyfin.url || '-';
    document.getElementById('config-jellyfin-api-key').textContent = data.jellyfin.apiKey;
    document.getElementById('config-jellyfin-user-id').textContent = data.jellyfin.userId || '(not set)';
    document.getElementById('config-jellyfin-library-id').textContent = data.jellyfin.libraryId || '-';
    document.getElementById('config-download-path').textContent = data.library?.downloadPath || './downloads';
    document.getElementById('config-kept-path').textContent = data.library?.keptPath || '/app/kept';
    document.getElementById('config-spotify-import-enabled').textContent = data.spotifyImport?.enabled ? 'Yes' : 'No';
    document.getElementById('config-matching-interval').textContent = (data.spotifyImport?.matchingIntervalHours || 24) + ' hours';
    
    if (data.cache) {
        document.getElementById('config-cache-playlist-images').textContent = data.cache.playlistImagesHours || '168';
        document.getElementById('config-cache-spotify-items').textContent = data.cache.spotifyPlaylistItemsHours || '168';
        document.getElementById('config-cache-matched-tracks').textContent = data.cache.spotifyMatchedTracksDays || '30';
        document.getElementById('config-cache-lyrics').textContent = data.cache.lyricsDays || '14';
        document.getElementById('config-cache-genres').textContent = data.cache.genreDays || '30';
        document.getElementById('config-cache-metadata').textContent = data.cache.metadataDays || '7';
        document.getElementById('config-cache-odesli').textContent = data.cache.odesliLookupDays || '60';
        document.getElementById('config-cache-proxy-images').textContent = data.cache.proxyImagesDays || '14';
    }
}

export function updateJellyfinPlaylistsUI(data) {
    const tbody = document.getElementById('jellyfin-playlist-table-body');
    
    if (data.playlists.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:var(--text-secondary);padding:40px;">No playlists found in Jellyfin</td></tr>';
        return;
    }
    
    tbody.innerHTML = data.playlists.map(p => {
        const statusBadge = p.isConfigured
            ? '<span class="status-badge success"><span class="status-dot"></span>Linked</span>'
            : '<span class="status-badge"><span class="status-dot"></span>Not Linked</span>';
        
        const actionButton = p.isConfigured
            ? `<button class="danger" onclick="unlinkPlaylist('${escapeJs(p.name)}')">Unlink</button>`
            : `<button class="primary" onclick="openLinkPlaylist('${escapeJs(p.id)}', '${escapeJs(p.name)}')">Link to Spotify</button>`;
        
        const localCount = p.localTracks || 0;
        const externalCount = p.externalTracks || 0;
        const externalAvail = p.externalAvailable || 0;
        
        return `
            <tr data-playlist-id="${escapeHtml(p.id)}">
                <td><strong>${escapeHtml(p.name)}</strong></td>
                <td class="track-count">${localCount}</td>
                <td class="track-count">${externalCount > 0 ? `${externalAvail}/${externalCount}` : '-'}</td>
                <td style="font-family:monospace;font-size:0.85rem;color:var(--text-secondary);">${p.linkedSpotifyId || '-'}</td>
                <td>${statusBadge}</td>
                <td>${actionButton}</td>
            </tr>
        `;
    }).join('');
}

export function updateJellyfinUsersUI(data) {
    const select = document.getElementById('jellyfin-user-select');
    select.innerHTML = '<option value="">All Users</option>' +
        data.users.map(u => `<option value="${u.id}">${escapeHtml(u.name)}</option>`).join('');
}

export function updateEndpointUsageUI(data) {
    document.getElementById('endpoints-total-requests').textContent = data.totalRequests?.toLocaleString() || '0';
    document.getElementById('endpoints-unique-count').textContent = data.totalEndpoints?.toLocaleString() || '0';
    
    const mostCalled = data.endpoints && data.endpoints.length > 0
        ? data.endpoints[0].endpoint
        : '-';
    document.getElementById('endpoints-most-called').textContent = mostCalled;
    
    const tbody = document.getElementById('endpoints-table-body');
    
    if (!data.endpoints || data.endpoints.length === 0) {
        tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;color:var(--text-secondary);padding:40px;">No endpoint usage data available yet.</td></tr>';
        return;
    }
    
    tbody.innerHTML = data.endpoints.map((ep, index) => {
        const percentage = data.totalRequests > 0
            ? ((ep.count / data.totalRequests) * 100).toFixed(1)
            : '0.0';
        
        let countColor = 'var(--text-primary)';
        if (ep.count > 1000) countColor = 'var(--error)';
        else if (ep.count > 100) countColor = 'var(--warning)';
        else if (ep.count > 10) countColor = 'var(--accent)';
        
        let endpointDisplay = ep.endpoint;
        if (ep.endpoint.includes('/stream')) {
            endpointDisplay = `<span style="color:var(--success)">${escapeHtml(ep.endpoint)}</span>`;
        } else if (ep.endpoint.includes('/Playing')) {
            endpointDisplay = `<span style="color:var(--accent)">${escapeHtml(ep.endpoint)}</span>`;
        } else if (ep.endpoint.includes('/Search')) {
            endpointDisplay = `<span style="color:var(--warning)">${escapeHtml(ep.endpoint)}</span>`;
        } else {
            endpointDisplay = escapeHtml(ep.endpoint);
        }
        
        return `
            <tr>
                <td style="color:var(--text-secondary);text-align:center;">${index + 1}</td>
                <td style="font-family:monospace;font-size:0.85rem;">${endpointDisplay}</td>
                <td style="text-align:right;font-weight:600;color:${countColor}">${ep.count.toLocaleString()}</td>
                <td style="text-align:right;color:var(--text-secondary)">${percentage}%</td>
            </tr>
        `;
    }).join('');
}

export function showErrorState(message) {
    const statusBadge = document.getElementById('spotify-status');
    if (statusBadge) {
        statusBadge.className = 'status-badge error';
        statusBadge.innerHTML = '<span class="status-dot"></span>Connection Error';
    }
    const authStatus = document.getElementById('spotify-auth-status');
    if (authStatus) authStatus.textContent = 'Error';
}

export function showPlaylistRebuildingIndicator(playlistName) {
    const playlistCards = document.querySelectorAll('.playlist-card');
    for (const card of playlistCards) {
        const nameEl = card.querySelector('h3');
        if (nameEl && nameEl.textContent.trim() === playlistName) {
            const existingIndicator = card.querySelector('.rebuilding-indicator');
            if (!existingIndicator) {
                const indicator = document.createElement('div');
                indicator.className = 'rebuilding-indicator';
                indicator.style.cssText = `
                    position: absolute;
                    top: 8px;
                    right: 8px;
                    background: var(--warning);
                    color: white;
                    padding: 4px 8px;
                    border-radius: 12px;
                    font-size: 0.7rem;
                    font-weight: 500;
                    display: flex;
                    align-items: center;
                    gap: 4px;
                    z-index: 10;
                `;
                indicator.innerHTML = '<span class="spinner" style="width: 10px; height: 10px;"></span>Rebuilding...';
                card.style.position = 'relative';
                card.appendChild(indicator);
                
                setTimeout(() => {
                    indicator.remove();
                }, 30000);
            }
            break;
        }
    }
}
