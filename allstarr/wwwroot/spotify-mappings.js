// Spotify Mappings Page JavaScript
// Handles filtering, sorting, pagination, and CRUD operations for Spotify track mappings

let currentPage = 1;
const pageSize = 50;
let currentFilters = {
    targetType: 'all',
    source: 'all',
    search: '',
    sortBy: null,
    sortOrder: 'asc'
};

/**
 * Loads mappings from the API with current filters and pagination
 */
async function loadMappings() {
    try {
        // Build query string with filters
        const params = new URLSearchParams({
            page: currentPage,
            pageSize: pageSize,
            enrichMetadata: true
        });
        
        if (currentFilters.targetType !== 'all') {
            params.append('targetType', currentFilters.targetType);
        }
        
        if (currentFilters.source !== 'all') {
            params.append('source', currentFilters.source);
        }
        
        if (currentFilters.search) {
            params.append('search', currentFilters.search);
        }
        
        if (currentFilters.sortBy) {
            params.append('sortBy', currentFilters.sortBy);
            params.append('sortOrder', currentFilters.sortOrder);
        }
        
        const response = await fetch(`/api/admin/spotify/mappings?${params}`);
        if (!response.ok) throw new Error('Failed to load mappings');
        
        const data = await response.json();
        
        // Update stats (using PascalCase from C# API)
        document.getElementById('stat-total').textContent = data.stats.TotalMappings.toLocaleString();
        document.getElementById('stat-local').textContent = data.stats.LocalMappings.toLocaleString();
        document.getElementById('stat-external').textContent = data.stats.ExternalMappings.toLocaleString();
        document.getElementById('stat-manual').textContent = data.stats.ManualMappings.toLocaleString();
        document.getElementById('stat-auto').textContent = data.stats.AutoMappings.toLocaleString();
        
        // Update pagination
        updatePagination(data.pagination);
        
        // Render table
        renderMappings(data.mappings);
    } catch (error) {
        console.error('Error loading mappings:', error);
        document.getElementById('content').innerHTML = 
            `<div class="error">Failed to load mappings: ${error.message}</div>`;
    }
}

/**
 * Updates pagination controls
 */
function updatePagination(pagination) {
    document.getElementById('page-info').textContent = 
        `Page ${pagination.page} of ${pagination.totalPages} (${pagination.totalCount} total)`;
    document.getElementById('prev-btn').disabled = currentPage === 1;
    document.getElementById('next-btn').disabled = currentPage === pagination.totalPages;
    document.getElementById('pagination').style.display = 'flex';
}

/**
 * Renders the mappings table
 */
function renderMappings(mappings) {
    const content = document.getElementById('content');
    
    if (mappings.length === 0) {
        content.innerHTML = `
            <div class="empty-state">
                <h3>No mappings found</h3>
                <p>Try adjusting your filters or search query.</p>
            </div>
        `;
        return;
    }
    
    const rows = mappings.map(mapping => {
        const metadata = mapping.Metadata || {};
        const artworkUrl = metadata.ArtworkUrl || '/placeholder.png';
        const title = metadata.Title || 'Unknown Track';
        const artist = metadata.Artist || 'Unknown Artist';
        const targetInfo = mapping.TargetType === 'local' 
            ? mapping.LocalId 
            : `${mapping.ExternalProvider}:${mapping.ExternalId}`;
        
        return `
            <tr>
                <td>
                    <div class="track-info">
                        <img src="${artworkUrl}" alt="${title}" class="track-artwork" 
                             onerror="this.src='/placeholder.png'">
                        <div class="track-details">
                            <div class="track-title">${escapeHtml(title)}</div>
                            <div class="track-artist">${escapeHtml(artist)}</div>
                        </div>
                    </div>
                </td>
                <td>
                    <span class="mono">${mapping.SpotifyId}</span>
                </td>
                <td>
                    <span class="badge ${mapping.TargetType}">${mapping.TargetType}</span>
                </td>
                <td>
                    <span class="mono">${targetInfo}</span>
                </td>
                <td>
                    <span class="badge ${mapping.Source}">${mapping.Source}</span>
                </td>
                <td>
                    <span class="mono">${new Date(mapping.CreatedAt).toLocaleDateString()}</span>
                </td>
                <td>
                    <div class="actions-cell">
                        <button class="action-btn" onclick="mapToLocal('${mapping.SpotifyId}', '${escapeHtml(title)}', '${escapeHtml(artist)}')">
                            Map to Local
                        </button>
                        <button class="action-btn" onclick="mapToExternal('${mapping.SpotifyId}', '${escapeHtml(title)}', '${escapeHtml(artist)}')">
                            Map to External
                        </button>
                        <button class="action-btn danger" onclick="deleteMapping('${mapping.SpotifyId}', '${escapeHtml(title)}')">
                            Delete
                        </button>
                    </div>
                </td>
            </tr>
        `;
    }).join('');
    
    const sortIndicator = (column) => {
        if (currentFilters.sortBy === column) {
            return currentFilters.sortOrder === 'asc' ? ' ▲' : ' ▼';
        }
        return '';
    };
    
    content.innerHTML = `
        <table>
            <thead>
                <tr>
                    <th class="sortable" onclick="sortBy('title')">Track${sortIndicator('title')}</th>
                    <th class="sortable" onclick="sortBy('spotifyid')">Spotify ID${sortIndicator('spotifyid')}</th>
                    <th class="sortable" onclick="sortBy('type')">Type${sortIndicator('type')}</th>
                    <th>Target ID</th>
                    <th class="sortable" onclick="sortBy('source')">Source${sortIndicator('source')}</th>
                    <th class="sortable" onclick="sortBy('created')">Created${sortIndicator('created')}</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody>
                ${rows}
            </tbody>
        </table>
    `;
}

/**
 * Sorts the table by the specified column
 */
function sortBy(column) {
    if (currentFilters.sortBy === column) {
        // Toggle sort order
        currentFilters.sortOrder = currentFilters.sortOrder === 'asc' ? 'desc' : 'asc';
    } else {
        // New column, default to ascending
        currentFilters.sortBy = column;
        currentFilters.sortOrder = 'asc';
    }
    
    currentPage = 1; // Reset to first page
    loadMappings();
}

/**
 * Applies filters and reloads mappings
 */
function applyFilters() {
    currentFilters.targetType = document.getElementById('filter-type').value;
    currentFilters.source = document.getElementById('filter-source').value;
    currentFilters.search = document.getElementById('search').value;
    
    currentPage = 1; // Reset to first page when filtering
    loadMappings();
}

/**
 * Escapes HTML to prevent XSS
 */
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

/**
 * Maps a Spotify track to a local Jellyfin track
 */
async function mapToLocal(spotifyId, title, artist) {
    const query = prompt(`Search Jellyfin for "${title}" by ${artist}:`, `${title} ${artist}`);
    if (!query) return;
    
    try {
        const response = await fetch(`/api/admin/jellyfin/search?query=${encodeURIComponent(query)}`);
        if (!response.ok) throw new Error('Search failed');
        
        const data = await response.json();
        
        if (data.tracks.length === 0) {
            alert('No tracks found in Jellyfin. Try a different search query.');
            return;
        }
        
        // Show selection dialog
        const trackList = data.tracks.map((t, i) => 
            `${i + 1}. ${t.title} by ${t.artist} (${t.album || 'Unknown Album'})`
        ).join('\n');
        
        const selection = prompt(`Found ${data.tracks.length} tracks:\n\n${trackList}\n\nEnter track number (1-${data.tracks.length}):`, '1');
        if (!selection) return;
        
        const index = parseInt(selection) - 1;
        if (index < 0 || index >= data.tracks.length) {
            alert('Invalid selection');
            return;
        }
        
        const selectedTrack = data.tracks[index];
        
        // Save mapping
        const saveResponse = await fetch(`/api/admin/spotify/mappings`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                SpotifyId: spotifyId,
                TargetType: 'local',
                LocalId: selectedTrack.id,
                Metadata: {
                    Title: selectedTrack.title,
                    Artist: selectedTrack.artist,
                    Album: selectedTrack.album
                }
            })
        });
        
        if (!saveResponse.ok) throw new Error('Failed to save mapping');
        
        alert(`✓ Mapped to local track: ${selectedTrack.title}`);
        loadMappings(); // Reload
    } catch (error) {
        console.error('Error mapping to local:', error);
        alert(`Failed to map to local: ${error.message}`);
    }
}

/**
 * Maps a Spotify track to an external provider track
 */
async function mapToExternal(spotifyId, title, artist) {
    const provider = prompt('Enter external provider (squidwtf, deezer, qobuz):', 'squidwtf');
    if (!provider) return;
    
    const externalId = prompt(`Enter ${provider} track ID:`, '');
    if (!externalId) return;
    
    try {
        const saveResponse = await fetch(`/api/admin/spotify/mappings`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                SpotifyId: spotifyId,
                TargetType: 'external',
                ExternalProvider: provider,
                ExternalId: externalId,
                Metadata: {
                    Title: title,
                    Artist: artist
                }
            })
        });
        
        if (!saveResponse.ok) throw new Error('Failed to save mapping');
        
        alert(`✓ Mapped to external track: ${provider}:${externalId}`);
        loadMappings(); // Reload
    } catch (error) {
        console.error('Error mapping to external:', error);
        alert(`Failed to map to external: ${error.message}`);
    }
}

/**
 * Deletes a Spotify track mapping
 */
async function deleteMapping(spotifyId, title) {
    if (!confirm(`Delete mapping for "${title}"?`)) return;
    
    try {
        const response = await fetch(`/api/admin/spotify/mappings/${spotifyId}`, {
            method: 'DELETE'
        });
        
        if (!response.ok) throw new Error('Failed to delete mapping');
        
        alert(`✓ Deleted mapping for "${title}"`);
        loadMappings(); // Reload
    } catch (error) {
        console.error('Error deleting mapping:', error);
        alert(`Failed to delete mapping: ${error.message}`);
    }
}

/**
 * Initializes event listeners
 */
function initializeEventListeners() {
    // Search with debounce
    let searchTimeout;
    document.getElementById('search').addEventListener('input', () => {
        clearTimeout(searchTimeout);
        searchTimeout = setTimeout(applyFilters, 300);
    });
    
    // Filter dropdowns
    document.getElementById('filter-type').addEventListener('change', applyFilters);
    document.getElementById('filter-source').addEventListener('change', applyFilters);
    
    // Pagination
    document.getElementById('prev-btn').addEventListener('click', () => {
        if (currentPage > 1) {
            currentPage--;
            loadMappings();
        }
    });
    
    document.getElementById('next-btn').addEventListener('click', () => {
        currentPage++;
        loadMappings();
    });
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', () => {
    initializeEventListeners();
    loadMappings();
});
