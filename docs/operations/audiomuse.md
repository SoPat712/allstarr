# AudioMuse

[AudioMuse](https://github.com/NeptuneHub/AudioMuse-AI) analyses how your songs sound: mood, energy and tempo. Allstarr can read that analysis to build mixes that follow how songs sound, not only their tags. It is optional; everything else works without it.

## Requirements

- A Jellyfin deployment for the core features below. AudioMuse analyses a Jellyfin library.
- AudioMuse 3.6.4 or a later 3.x release, reachable from the Allstarr container.
- AudioMuse set up for the same Jellyfin server. If AudioMuse knows several Jellyfin servers, the one whose address matches Allstarr's Jellyfin address is used.
- Some of your library analysed by AudioMuse. Allstarr reads stored analysis only; it never asks AudioMuse to analyse songs.

## Connect AudioMuse

Open **Settings → Mixes and discovery**. The **AudioMuse** card shows the connection state and how much of your library is analysed.

1. Choose **Edit connection**.
2. Enter the **AudioMuse address** and its **API key**, then **Save**.

The key is stored encrypted; leave the field blank later to keep it. The status line then reads **Connected** with the number of analysed songs, or explains what is missing:

| Status | What to do |
| --- | --- |
| Not set up | Enter the address and key. |
| Can't reach AudioMuse | Check the address and that the container can reach it. |
| AudioMuse refused the key | Enter the current key. |
| This AudioMuse version isn't supported | Update AudioMuse to 3.6.4 or a later 3.x release. |
| AudioMuse isn't linked to this Jellyfin server | Add the same Jellyfin server in AudioMuse. |
| Waiting for AudioMuse to analyze songs | Let AudioMuse analyse part of the library. |
| AudioMuse isn't ready for Allstarr | Finish AudioMuse's own setup for that server. |

Allstarr starts and plays normally when AudioMuse is missing or down; the features below fall back to tags and listening history.

## What it powers

In the normal `core` release:

- **Mood mixes** in For you (chill, focus, workout, party and sleep) use AudioMuse's measurements for analysed songs. Without them, mood mixes rely on tags.
- **Mixes started from a song in your library** add songs that sound similar, marked **Similar sound**.
- **Your sound** in **For you → Your taste** compares how the songs a listener finishes sound with the rest of their library. It appears once enough of those songs are analysed.

The same page has **Let listeners add mixes to their apps**. With it on, each listener chooses which mixes appear as playlists in their music apps.

## Development release only

These need `ALLSTARR_RELEASE_PROFILE=development` (see [release profiles](configuration.md#release-profiles)) and work only for listeners whose Intelligence settings include AudioMuse:

- Sonic Instant Mix in Jellyfin apps. Otherwise Instant Mix works as usual.
- The OpenSubsonic `getSonicSimilarTracks` and `findSonicPath` methods.
