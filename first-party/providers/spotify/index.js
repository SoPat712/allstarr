function emptyPage() {
  return { items: [], nextCursor: null, isPartial: false };
}

function unavailableTracks(id) {
  return {
    playlist: { id, name: "Unavailable", owner: { providerUserId: "selected-user" }, sourceRevision: "unavailable" },
    tracks: emptyPage()
  };
}

registerExtension({
  getUserPlaylists() {
    secrets.get("sessionCookie");
    return emptyPage();
  },
  getPlaylistTracks(request) {
    secrets.get("sessionCookie");
    return unavailableTracks(request.playlistId);
  },
  searchPlaylists() {
    secrets.get("sessionCookie");
    return emptyPage();
  }
});
