function emptyPage() {
  return { items: [], nextCursor: null, isPartial: false };
}

registerExtension({
  getUserPlaylists() {
    secrets.get("musickitcredentials");
    return emptyPage();
  },
  getPlaylistTracks(request) {
    secrets.get("musickitcredentials");
    return {
      playlist: { id: request.playlistId, name: "Unavailable", owner: { providerUserId: "selected-user" }, sourceRevision: "unavailable" },
      tracks: emptyPage()
    };
  }
});
