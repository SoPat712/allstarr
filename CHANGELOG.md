# Changelog

## Unreleased

- Settings is one list of categories, such as Sound quality, Music sources, Library, Apps, People and sign-in, Storage and speed, and Backups and health. Each says what's set, and **Find a setting** jumps to any setting. Changes save as you make them, usually with Undo. Listeners see their own Listening, Appearance, Adding to the library and Alerts pages. Earlier addresses open the page that now holds those settings.
- Integrations is now **Sources**, with Services and Accounts tabs. Extensions are added from Services with **Add extension**, and source order for search, downloads, streaming, playlists and lyrics moved to **Settings → Music sources**.
- A native client API (v2) gives apps built for Allstarr one track shape, batched reads and signed playback links, alongside v1. It runs on the music port with Jellyfin and stays off until an administrator turns on **Settings → Apps → Client API for apps**.
- **Library → Import** (Bring your music) moves a Spotify or Apple Music collection into Allstarr. Liked songs (library songs on Apple Music) become hearts, playlists appear in your apps, saved albums are hearted and followed artists are followed for New releases. Each song is matched to your library or a service, unsure matches wait for review, and nothing changes in Spotify or Apple Music.
- Lyric taps, resume and time-based seeking work for provider songs in Jellyfin and Subsonic clients, reusing cached audio. Incomplete downloads are rejected before conversion, and stream rejections include safe diagnostic reasons.

- Activity uses outcome colors, readable operation names, song and playlist context, recovery guidance and links to exact jobs. Health results distinguish failed checks from unavailable details; Tasks includes Health and backup schedules.

- Playback can switch sources after a failure. Fast, Balanced and Best quality start preferences control the wait for alternatives, and seeking works while a track fills the cache.
- Playback caching defaults to 72 hours after last use. Cached explains when playback caching is off and shows expiry using the current retention setting; hearts and kept copies remain separate.
- Jellyfin File and Download playback fills the cache too, including when a player starts with a byte range. Replays preserve the original audio and account access.
- Jellyfin Health uses the saved server API key for read-only checks, with no dedicated listener account or simulated listening session. Results distinguish server checks from earlier listener checks and leave unavailable media neutral.
- Desktop navigation stays pinned while pages scroll, with the profile and all destinations reachable in short windows.
- Jellyfin root discovery returns music libraries and native playlist folders, allowing sign-in and playlist browsing to complete.
- Hearted provider songs appear in favorites, and playlists can mix library and provider songs. Library browsing shows only the media server's own music. Linked listening profiles can share hearts; mixed playlists and offline downloads preserve stable track entries.
- Subsonic's listener Health checks exercise playback without adding synthetic plays to listening history or scrobbles. Jellyfin uses the read-only server checks described above. Backup controls include retention and restore verification.
- A **Connect apps** guide and problem inbox help testers connect clients and report problems with useful context. Administrators can open match or lyrics review directly from a report.
- Provider checks distinguish failed checks from missing samples and unverified facts. Source pages show recent checks and explain temporary routing skips. Source labels distinguish Allstarr items from the service actually delivering their audio, and missing covers use provider artwork.
- Search and browsing keep distinct provider tracks separate and preserve original media-server details.
- Automatic playback alternatives must qualify individually and preserve artist credits. Existing automatic links to low-scoring covers are repaired from saved evidence while manual selections remain intact.
- Lyrics sources include word timing and visible provider credits. Private keys belong to configured accounts.
- Dashboard access follows current media-server permissions, repeated password checks are limited, and match reviews, extension diagnostics and playlist writes enforce listener privacy and ownership.
