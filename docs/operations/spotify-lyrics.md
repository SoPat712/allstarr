# Spotify lyrics

Spotify lyrics run inside Allstarr through the typed lyrics capability. They use the same selected encrypted Spotify account and expiring access-token cache as playlist reads. A separate lyrics container is not required.

## Setup

1. Open **Integrations → Accounts** and add or edit a Spotify account. Use Personal for one listener or Shared for the household.
2. Save the account's `sp_dc` session cookie through the account form. Allstarr encrypts it; do not put it in a URL or share it in logs.
3. Enable the account and place Spotify in the desired **Routing → Lyrics** order. The first timed result wins; plain text is retained as fallback when later sources have no timing.
4. Use the account's **Test** action to check its connection and lyrics endpoint. A healthy connection does not guarantee that every song has lyrics.

Public playlist imports can still use anonymous reads. Lyrics require an authorized account and never borrow the shared anonymous token. Expired or rejected cookies need attention in the account form; unavailable lyrics fall through to the remaining configured sources.

Successful lyric responses use the bounded lyrics cache, separated by account, revision, secret reference, track and requested format. Tokens and cookies are not part of cached content or diagnostics. Cancellation and bounded response reads apply to the native request. The [upstream Spotify lyrics client](https://github.com/akashrchandran/spotify-lyrics-api/blob/main/src/Spotify.php) documents the web-player endpoint used for compatibility; the actual free-account and regional behavior still depends on Spotify.

## Upgrading from the lyrics container

The `spotify-lyrics` Compose service and profile have been retired. Old `spotify` or `spotify-lyrics` entries in `.allstarr-profiles` are tolerated and do not enable a container. A normal subsequent stack update removes the obsolete service through the existing orphan cleanup. The deployment controller does not remove stored data.

`SPOTIFY_LYRICS_API_URL` and `SpotifyApi__LyricsApiUrl` are ignored, and the former durable sidecar URL setting is no longer configurable. Existing Spotify accounts retain their encrypted cookies. During an older `.env` import, the Spotify account cookie remains supported; the obsolete service URL is identified as retired. If the cookie existed only in a separately managed container, save it through Allstarr's account form yourself. Do not send the old service URL as an account credential.
