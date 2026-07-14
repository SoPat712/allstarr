function emptyPage() {
  return { items: [], nextCursor: null, isPartial: false };
}

function artist(value) {
  return { id: String(value.id), name: value.name };
}

function track(value) {
  return {
    id: String(value.id),
    title: value.title,
    artists: value.contributors ? value.contributors.map(artist) : [artist(value.artist)],
    albumId: value.album ? String(value.album.id) : null,
    albumTitle: value.album ? value.album.title : null,
    durationMs: value.duration ? value.duration * 1000 : null,
    isrc: value.isrc || null,
    isExplicit: value.explicit_lyrics,
    artworkUrl: value.album ? value.album.cover_xl : null
  };
}

function get(path) {
  const response = http.get(`https://api.deezer.com/${path}`, {});
  return response.statusCode === 200 ? JSON.parse(response.body) : null;
}

registerExtension({
  searchTracks(request) {
    const value = get(`search/track?q=${encodeURIComponent(request.query)}&limit=${request.page.limit}`);
    return value ? { items: value.data.map(track), nextCursor: null, isPartial: !!value.next } : emptyPage();
  },
  getTrack(request) {
    const value = get(`track/${encodeURIComponent(request.id)}`);
    return value ? track(value) : null;
  },
  lookupByIsrc(request) {
    const value = get(`track/isrc:${encodeURIComponent(request.isrc)}`);
    return value ? track(value) : null;
  },
  searchAlbums(request) {
    const value = get(`search/album?q=${encodeURIComponent(request.query)}&limit=${request.page.limit}`);
    return value ? { items: value.data.map(item => ({ id: String(item.id), title: item.title, artists: [artist(item.artist)], trackCount: item.nb_tracks, artworkUrl: item.cover_xl })), nextCursor: null, isPartial: !!value.next } : emptyPage();
  },
  getAlbum(request) {
    const item = get(`album/${encodeURIComponent(request.id)}`);
    return item ? { id: String(item.id), title: item.title, artists: [artist(item.artist)], trackCount: item.nb_tracks, artworkUrl: item.cover_xl } : null;
  },
  searchArtists(request) {
    const value = get(`search/artist?q=${encodeURIComponent(request.query)}&limit=${request.page.limit}`);
    return value ? { items: value.data.map(item => ({ id: String(item.id), name: item.name, artworkUrl: item.picture_xl })), nextCursor: null, isPartial: !!value.next } : emptyPage();
  },
  getArtist(request) {
    const item = get(`artist/${encodeURIComponent(request.id)}`);
    return item ? { id: String(item.id), name: item.name, artworkUrl: item.picture_xl } : null;
  }
});
