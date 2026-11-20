# User guide

Allstarr has two parts:

- **Music apps** connect to the Jellyfin or Subsonic port, normally `5274`. You listen there.
- **The dashboard** runs on port `5275`. You use it to connect services, import playlists, review matches, find mixes and change settings. It is not a music player.

The first half of this guide is for everyone. The second half, [For administrators](#for-administrators), covers sources, settings, adding songs to the library, backups and health.

## Sign in

Open the dashboard and sign in with your media-server username and password.

- **Keep me signed in** keeps you signed in on that browser for longer. See [dashboard access](operations/webui-access.md) for the session lengths.
- If your administrator set up single sign-on, choose **Continue with** and your provider's name. See [OIDC login](operations/webui-access.md).
- On a Subsonic server, **Let Allstarr manage my playlists** lets Allstarr create and update playlists for you. It starts unchecked. You can turn it on later; see [Allow playlist editing](#allow-playlist-editing).

## Find your way around

The sidebar shows **Home**, **Library**, **For you**, **Sources**, **Activity**, **Settings** and **Report a problem**. Your name in the sidebar opens your **Listening settings**. On a phone, Home, Library, For you and Activity sit at the bottom, and **More** holds Listening settings, Sources, Report a problem and, for administrators, Settings.

Listeners and administrators see different parts:

| Page | Listeners | Administrators also get |
| --- | --- | --- |
| Library | Playlists, Matches, To library, Import | Cached and Kept, and artist matches in Matches |
| Sources | **Available services** and **My accounts** | Every account, shared accounts, extensions and checks |
| Activity | History and Plays | Jobs, Tasks, Reports, and an **Everyone** view of household work |
| Settings | Listening, Appearance, Adding to the library, Alerts | Every household and server setting |

Pages update on their own when something changes, including changes made in another tab. Unsaved edits in a form are kept.

## Connect your music app

Your settings show what a music app asks for. Open **Listening settings** (your name in the sidebar; on a phone, **More → Listening settings**) and find **Connect a music app** at the bottom. Administrators find the same card in **Settings → Apps**. It shows the server address your administrator set and your username. Use **Copy** next to each, then sign in to your app with your usual media-server password. If the address is empty, ask your administrator for it; typing one there only changes what you copy. On Jellyfin, **Open web player** opens the player in your browser.

Which apps work depends on whether this Allstarr serves Jellyfin or Subsonic:

| App | Platform | Jellyfin | Subsonic / OpenSubsonic | Setup help |
| --- | --- | --- | --- | --- |
| Finer Player | iPhone | Yes | — | — |
| Musiver | Mobile | Yes | — | — |
| Feishin | Desktop | Yes | Yes | [Feishin](https://github.com/jeffvli/feishin) |
| Finamp | Android and iOS | Yes | — | [Finamp](https://github.com/finamp-app/finamp) |
| Jellyfin Web | Web browser | Yes | — | **Open web player** on the card; [project](https://github.com/jellyfin/jellyfin-web) |
| Tempo | Android | — | Yes | [Tempo](https://github.com/CappielloAntonio/tempo) |
| Symfonium | Android, paid after a trial | Yes | Yes | [Jellyfin setup](https://docs.symfonium.app/wiki/providers/jellyfin-media-provider-configuration/) or [Subsonic setup](https://docs.symfonium.app/wiki/providers/subsonic-opensubsonic-media-provider-configuration/) |

Steps for each app:

- **Finer Player** and **Musiver**: add a Jellyfin server with the address, then sign in. Your imported playlists and mixes are under Playlists.
- **Feishin**: open **Manage servers**, add a server and choose Jellyfin or Subsonic. Enter the address, your username and your password.
- **Finamp**: add a Jellyfin server with the address, sign in, then choose your music library.
- **Jellyfin Web**: choose **Open web player**, sign in and open your music library.
- **Tempo**: add a Subsonic server with the address, sign in, then refresh your library.
- **Symfonium**: add a Jellyfin or Subsonic / OpenSubsonic media provider, enter the address, your username and your password, then sync the provider.

For a first listen, play a song, seek ahead, heart it, add it to a playlist and try an offline download. A download or offline cache belongs to that app; check it plays with the device offline before you travel. App-specific limits are in [client compatibility](operations/client-compatibility.md).

## Search and play

Your music apps show your own library as usual. Songs from connected services appear in search, in playlists you import, in mixes and in your favorites. They do not appear when you browse your media-server library.

- Songs from outside your library are marked **[A]** and explicit songs **[E]**. You can turn either marker off in [Listening settings](#listening-settings).
- When a song exists in your library, it plays from there. Otherwise Allstarr plays it from a connected service. If the first source is slow to start, Allstarr also tries the next one and keeps whichever starts first.
- Each service has a **Use this source** switch on its page in **Sources**: **On** (used automatically), **Manual only** (shown in search and matching for you to choose, never used on its own), or **Off**. **Follow** uses the household setting.
- Services can rate-limit requests or ask for verification. If a song won't play, try again in a few minutes or pick another source, and [report it](#report-a-problem) with the song and app version if it keeps failing.

A few built-in sources need no account:

- **Monochrome** is an account-free streaming source.
- **ArtistGrid** lists individual versions from artist trackers, such as leaks and alternate takes. It is **Manual only** by default. Search for the artist's name and an optional song title, then pick the exact version in a match.

How playback picks a source, and what `[A]` titles mean in each app, is in [client compatibility](operations/client-compatibility.md).

## Library

**Library** has Playlists, Matches, To library and Import. Administrators also see Cached and Kept; see [Cached and kept songs](#cached-and-kept-songs).

### Playlists

Choose **Import playlist** to bring a playlist into your apps. Nothing changes on the service.

1. **Choose.** Under **Paste a link**, paste a public playlist link, such as a Spotify or ListenBrainz playlist. No account is needed. Under **From my accounts**, pick playlists or **Liked Songs** from your connected accounts. **Other playlist sources** has more options: a public playlist by link, **Browse a Spotify user** to pick one of a public profile's playlists, and Last.fm **Loved tracks**, **Top tracks** or **Similar tracks** from your own Last.fm account.
2. **Options and review.**
   - **Import once** takes a snapshot and ignores later changes. **Keep in sync** checks for changes on the schedule you pick: **Every hour**, **Daily at 3:00 AM** or **Mondays at 3:00 AM**. For ListenBrainz generated playlists, **Follow newest edition** keeps the link on the newest one.
   - **New playlist** creates a playlist in your apps with the source name. **Existing playlist** keeps the playable songs in a playlist you already manage. Allstarr never takes over a playlist just because its name matches.
   - **Keep downloaded copies** saves permanent copies of the external songs Allstarr can download. It is off by default. Songs in your library are already permanent.

After the import, unsure songs wait in [Matches](#matches), and songs with no match show as not playable in the playlist's details. Through Allstarr, your apps play the playlist with its service songs. The copy stored in your media server holds only songs from your library, so it still works when opened without Allstarr.

Open a playlist for its songs and details. Its **Actions** menu has:

- **Update now** to check the source for changes, and **Refresh source**.
- **Export missing songs as CSV** or **as JSON** to download the songs with nothing to play, in source order.
- **Edit settings** for the schedule and sync options, and **Pause** to stop automatic updates.

**Remove from apps**, at the bottom of a playlist Allstarr keeps in sync with Jellyfin, stops syncing and removes the playlist from your music apps. The source playlist, your matches and your settings stay. The confirmation lists songs you added yourself in Jellyfin. **Undo**, or importing the same playlist again with **Keep in sync**, brings it back with its name, artwork, order and your added songs.

When a sync removes songs, it removes only songs it added. Songs you added yourself stay.

Who can see an imported playlist follows your media server's playlist permissions. Each listener plays external songs through the accounts available to them, so a song can be playable for one person and not another. Change sharing in your media-server app; Allstarr does not keep its own share list.

#### Allow playlist editing

To reorder or remove songs in your media-server playlists, Allstarr needs your permission.

- **Jellyfin:** in **Sources → My accounts**, **Playlist editing in Jellyfin** shows whether you allowed it. Choose **Allow editing** and enter your Jellyfin password once. Allstarr keeps a sign-in token, never your password. **Remove** takes it away. Without it, Allstarr uses the household fallback an administrator set up, if any.
- **Subsonic:** in **Sources → My accounts**, **Playlist management** works the same way. Choose **Allow playlist management** and enter your Subsonic password, or **Revoke access** to stop future writes. Reading playlists keeps working either way.

### Matches

**Matches** shows how each song from your imported playlists plays. Use the filters **Needs a look**, **Matched by Allstarr**, **Chosen by you**, **Rejected** and **All**, or search by song, artist, album or playlist.

Open a song to compare it with what plays, then:

- Pick another candidate under **Other choices**, or use **Search for another version…** to search your library and services, including **Manual only** sources. Then choose **Use** *service* **version**. Administrators can save it **For me** or **For the household**. A personal choice wins over a household choice, which wins over automatic matching.
- **Nothing fits** rejects the current candidates.
- In the **More** menu: **Rematch**, **Wrong lyrics?** and **Report a problem**.

Choose **Select** to act on several songs at once. **Rematch all** re-runs automatic matching for every song; your own choices and rejections stay. Saved choices survive playlist refreshes and restarts.

Allstarr accepts a match on its own only when it is confident both are the same recording. A different version, such as karaoke, instrumental, a cover or a DJ-mix excerpt, is not accepted on its own. Among good matches, a song in your library wins; otherwise the streaming order in **Settings → Music sources** decides. How matching scores songs is in the [architecture overview](architecture/overview.md).

### To library

**To library** follows songs and albums being added to your media-server library. Filter by **Active**, **Needs a look**, **Added** or **All**.

You can start an addition with **Add to library…** in a mix song's menu (administrators can also start one from Kept). If you need approval, your request shows under **Waiting for approval** until an administrator approves or declines it; **Cancel request** withdraws it. Your own file naming and quality choices are in **Settings → Adding to the library**. Administrators set up the folder; see [Adding to your library](#adding-to-your-library).

### Import

**Import** (**Bring your music**) moves a Spotify or Apple Music collection into Allstarr. Nothing changes in Apple Music or Spotify.

1. Pick **Apple Music** or **Spotify** and your connected account. Connect one first in **Sources** if needed.
2. Under **Choose what to bring**, pick **Liked Songs** (Apple Music: **Library songs**), **Playlists**, **Albums** and **Artists**. Liked songs become hearts, albums are hearted, and artists are hearted and followed in **New releases**. **Make playlists in** your media server puts each playlist in your apps. **Choose individual collections** lets you pick single playlists and mark each **Keep in sync**.
3. Choose **Start import**. Matching runs in the background and shows in Activity. Review only the unsure songs, then see what came over under **Done**.

**Stop moving** stops an import; anything already moved stays.

## For you

**For you** is private to your account. It has four tabs.

### Mixes

**For you** shows your mixes: **Made for you**, **Moods**, **Your mixes**, **From your services** and a shelf of **New releases**. Mixes refresh every morning.

- Open a mix and turn on **Show in my apps** to see it as a playlist in your music apps. **✓ In your apps** confirms it. **Manage in Playlists** removes it. "Your admin hasn't allowed adding mixes" means an administrator turned this off for the household.
- **Update now** refreshes a mix. Its last songs stay available while it refreshes, and a failed refresh keeps them.
- **Start a mix** builds your own mix around a song or artist. It appears under **Your mixes**.
- Each song's menu has **More like this**, **Not for me**, **Don't play this artist** and **Add to library…**.
- **How this mix is made** explains the recipe: which plays it used, how many songs it looked at, how many it kept, and how many came from your music versus your services.

Each recipe draws on your own listening and fills from your services when it runs short. Mood mixes (Chill, Focus, Workout, Party and Sleep) use sound analysis when your administrator has connected AudioMuse, and song tags otherwise; each needs 25 fitting songs to be ready.

### New releases

**New releases** lists new music from artists you follow. Use **Follow more artists** to find artists, or the menu for **Follow my top artists** and **Follow artists from my hearts**. Filter by **Albums**, **EPs** or **Singles**. Open a release for **Add to apps** or **Dismiss release**. Just Dropped collects new releases every Friday.

### Your taste

**Your taste** shows what Allstarr has learned from your listening. Under **Steer your mixes**, tap a style or artist to hear more or less of it, **Today only** or **Until I change it**, or hide it. **Private listening (pause learning)** stops learning without losing what it knows. **Reset taste** starts again. **See the details** shows recent (**Lately**) and long-term (**Usually**) preferences.

### Recap

**Recap** shows your plays, top songs and artists, and listening streaks for **Last 7 days**, **Last 30 days**, **This year**, **All time** or **Past years**. Listening time is estimated from song lengths.

### Import listening history

Open **For you → Recap → Bring in your history → Import history** and choose one or more files. Allstarr reads exports from Spotify (request **Extended streaming history** in Spotify's privacy settings), Apple (request a copy of your Apple Media Services information and import the ZIP), Last.fm, ListenBrainz, Maloja and Koito. Files can be up to 500 MB unless your administrator set a lower limit.

Review each file's preview before you confirm it. Imports run in the background and never scrobble, download music or create hearts. Imported plays appear in Recap at their original dates. **Use imported history** in **Your taste** decides whether they shape your long-term taste; it is on by default. Deleting an import removes its plays.

## Lyrics

Music apps get lyrics from the sources your administrator set in **Settings → Music sources → Lyrics**, tried in that order. The order can include lyrics saved next to kept copies and your media server's own lyrics.

To fix a song's lyrics, open it in **Matches** and choose **Wrong lyrics?**. Preview each source, then choose **Use this source for this recording**. This saves a source, not the words; if the source goes away, Allstarr goes back to the normal order. Administrators can save the choice for **Only me** or the **Household**. **Use household default** or **Use automatic selection** removes a saved choice.

Sources, their order and how each one works are in [lyrics sources](operations/lyrics.md).

## Favorites and kept songs

Hearting a service song in your app saves it to your favorites. By default Allstarr also saves a permanent copy (a kept copy) using an account you can use, so the song keeps playing even if that account is later disconnected. A song already in your library is not downloaded again, and a copy someone else in the household already kept is reused.

Your saved service songs show in your app's favorites views (Jellyfin favorites, Subsonic starred lists), next to your library favorites. Removing the heart cancels pending work and releases your hold on the copy. When nothing holds a kept copy any more, it is deleted after a 24-hour grace period. Hearting it again in that time keeps it.

With the default setting, hearting never puts music in your media-server library, retags files or sends a love to Last.fm. To add a song to the library, use [To library](#to-library).

## Scrobbling

Connect Last.fm, ListenBrainz, multi-scrobbler or Koito in **Sources → My accounts**. Each listener connects their own. Songs streamed through Allstarr are always scrobbled; songs from your own library are scrobbled only if your administrator turned that on.

Listens that couldn't be sent show at the top of **My accounts**. **Retry all** sends them again; **Reconnect saved account** fixes an expired sign-in first. If a listen may already have arrived, check the service's history and choose **Already in history** or **Retry**. **Confirm destination** picks the account for older listens. Queued listens never move to a different account on their own.

Delivery and repair are in [scrobble delivery and recovery](operations/scrobbling.md).

## Activity

**Activity** shows what Allstarr has been doing for you.

- **History** lists finished work by song or playlist, with its source and result. Routine upkeep shows only when it fails. Open an entry for its details and next step.
- **Plays** lists what you played, where it played from and how long it took to start. Filter by **Played**, **Stopped**, **Problems** or **Slow starts**. Open a play for **Wrong song?**, **Look for it again**, **Report a problem** or **Copy diagnostics**.

Administrators also see **Jobs** (running, waiting, failed and finished work, with **Cancel job…**), **Tasks** (playlist schedules, health checks and backups, with **Run now**) and **Reports**. **Mine** and **Everyone** switch between your work and the household's.

## Listening settings

<a id="listening-preferences"></a>

Open your name in the sidebar, or **More → Listening settings** on a phone. These choices are yours and work in both Jellyfin and Subsonic apps. Changes save as you make them.

- **Explicit songs:** **All**, **Hide clean/edited versions** (keeps naturally clean, explicit and unrated songs) or **Clean only** (also hides songs whose rating isn't known). An administrator can lock you to Clean only.
- **Mark external songs** shows **[A]**; **Mark explicit songs** shows **[E]**. Both are on by default.
- **Playback start:** **Fast start** prefers the quickest lossless source, **Balanced** (the default) favors quality when songs still start within about three seconds, **Best quality** always prefers the highest quality.
- **Quality limit** can be lower than the household's limit, not higher.
- **Your music libraries**, shown when there are several, picks the libraries your mixes and autoplay draw from.
- **Reset to household defaults** drops your choices.

Reload search results, playlists or queues in your app to see changes.

Other settings for you are under **Settings**: **Appearance** (theme on this device), **Adding to the library** (your own file naming, quality and tags for library copies) and **Alerts** (where Allstarr tells you when your own accounts need attention, by ntfy, Discord or webhook).

## Report a problem

Use **Report a problem** in the sidebar, on Home's now-playing card, in a playlist song's details, in Matches, or on a play in Activity. Choose what went wrong (**Won't play**, **Can't seek**, **Wrong song**, **Wrong lyrics**, **Wrong details** or **Something else**) and add a short note with the app and what you tried. Leave out passwords, tokens and private links.

A report started from a song includes its details, such as the saved match and where it played from. Reports stay inside Allstarr for your administrator. Reporting doesn't change a match or lyrics by itself.

## For administrators

### First setup

1. Start the stack and open the dashboard.
2. Under **Connect your media server**, choose **Jellyfin** or **Subsonic**, enter its address and sign in as a media-server administrator. The password is discarded. The connection is saved in the data folder and Allstarr restarts.
3. Sign in and work through **Set up Allstarr**. Mark each step **Reviewed, continue** or **Skip for now**; progress is saved for your account. **Finish setup** skips the remaining optional steps. Reopen it any time from **Settings → Backups and health → Walk through setup again**, or **Continue setup** on Home. Nothing is reset.
4. Connect services in **Sources** and set their order in **Settings → Music sources**.
5. Set the address listeners use in **Settings → Apps → Music-client address**, so each listener's settings can show it.
6. Test ordinary library playback in a music app before adding playlists or services.

Upgrading from version 2: the setup guide has a step to review your previous settings before anything is imported. See [legacy `.env` import](operations/legacy-env-import.md).

### Sources

<a id="integrations"></a>

**Sources** has two tabs.

- **Services** lists every service Allstarr can use: built-in ones, public ones and installed extensions. Filter by **Needs attention**, **In use**, **Off** or **All**. **Add extension** installs an extension from a catalog, or from a download link with its SHA-256 checksum, and lets you review its access before you enable it.
- **Accounts** lists every saved account. Listens that couldn't be scrobbled show at the top, and **Shared provider hearts** sits below the list.

A service is something Allstarr can talk to. An account is a saved sign-in for a service, with who may use it. Extensions add services.

Open a service to see:

- **Use this source**: **Everyone** sets the household default; each listener can override it under **Just you**.
- **Last 30 checks**, and under **Automatic routing**, any pause after repeated failures with **Resume now**. Other sources keep playing during a pause.
- **Settings, accounts and tests**: **Connect account**, **Test connection**, **Measure start time** and any **Server settings**.

Opening a page never runs a check; **Check now** does.

To connect a service:

1. Open it in **Sources → Services**.
2. Choose **Connect account**, or fill its **Server settings** if it has some.
3. Pick the smallest audience that needs it: **Personal** for one listener, or **Shared** for everyone.
4. Save, then test it.

Saved passwords and keys are never shown again. Leaving a secret field blank while editing keeps the saved one.

A listener's own Personal account is used before a Shared one. Listeners manage their own Personal accounts; administrators manage Shared accounts and can assign a Personal account to a listener. Listeners can connect their own accounts unless you turn off **Listeners can connect their own accounts** in **Settings → Music sources**.

**Edit access** changes an account's audience. Sharing an account lets everyone use it, and its service limits. Making it Personal again stops new use but can't take back audio already played. More in [configuration](operations/configuration.md).

Some services have their own setup:

- **Apple Music** has a three-step checklist: connect the Apple Music account, sign in for playback with the same Apple account, then **Test playback**. See [Apple setup](operations/apple-download-provider.md).
- **SpicyLyrics** needs a free key: choose **Get a free API key**, then **Connect account**.
- Extensions that need a verified sign-in show a **Sign in** step on their account. See the [extension SDK](extensions/sdk-v1.md).

**Shared provider hearts** links a Jellyfin profile and a Subsonic profile that belong to the same person, so hearts on service songs apply to both. Both must have signed in before. Review the names and profile codes before you confirm; matching names alone don't prove it's the same person. The first link combines existing hearts and can't be undone here. Everything else stays separate.

### Settings

Settings saves as you change it. Settings marked **Restarts Allstarr** restart it when saved. **Find a setting** at the top searches every setting. Each page says whether it affects **Just you** or **Everyone**.

**Sound quality**

- **Audio quality** is the household ceiling. **What each source sends** shows what each service is asked for and what it actually delivered.
- **Playback start for everyone** is the default listeners start from.
- **Prefer your own music when unsure** favors your library among matches that aren't certain. It never raises a match's confidence.

**Music sources**

- Tabs **Search**, **Downloads**, **Streaming**, **Playlists** and **Lyrics** each hold a source order. Drag to reorder; **Sort by score** orders sources by their measured score. Your library always plays first when it has the song.
- **Lyric translation**: **Translate lyrics for apps** and an optional LibreTranslate address.
- **Listeners can connect their own accounts** is on by default. Turning it off stops new listener accounts; existing ones keep working.

**Library**

- **Libraries to use** picks which media-server libraries Allstarr reads. Changes apply without a restart.
- [Adding to your library](#adding-to-your-library) settings sit here too.
- **Downloads**: **Just the song** or **Whole album**.
- **Imported playlists** and **Folders** hold playlist matching and download paths.

**Apps**

- **Client API for apps** lets signed-in apps use Allstarr's extra features, such as song details, synced lyrics and mixes. It is off by default. See the [client API](api/README.md).
- **Music-client address** is the address listeners use in their apps. **Connect a music app**, below it and in each listener's settings, shows it with their username.
- **Browser apps** lists browser origins allowed to call Allstarr.

**Mixes and discovery**

- **AudioMuse** connects sound analysis. Choose **Edit connection** and enter its address and API key. Mood mixes and **Your sound** in Your taste use it. Sound-based Instant Mix and the OpenSubsonic sonic-similarity calls also need the `development` release profile.
- **Let listeners add mixes to their apps** is on by default.
- Similar-song fallbacks from Last.fm and ListenBrainz, used when a service has no suggestions.

**History and scrobbling**

- **Scrobble songs from your own library** is off by default. Leave it off if your media server already scrobbles them, so each play counts once.
- **Activity history** sets how long Activity keeps records.

**People and sign-in**

- **Listeners** lists everyone with their recent plays. Open a listener to turn on **Clean only, locked**, or, when trusted listeners may add songs, **Adds to the library without approval**. **See their activity** opens their plays.
- **Explicit songs for everyone** sets the household filter.
- **Media-server access** (Jellyfin) keeps playlist sync and library scans working when no one is signed in. **Change access**, then **Verify access** with a Jellyfin administrator sign-in; this restarts Allstarr. On Subsonic, each listener grants access themselves.
- **Playlist editing** shows how many listeners allowed it, and a **Household fallback** sign-in for those who haven't.
- **Single sign-on** connects an OpenID Connect provider. **Save and restart** applies it. See [OIDC login](operations/webui-access.md).
- **Network** shows which networks can reach the dashboard. These are set in Compose or `.env`.

**Alerts**

**Open problems** lists what needs attention now. **Household alerts** sends disk-space and health problems to ntfy, Discord or a webhook; listeners add their own channels for their accounts. **Low disk space** sets the warning and critical limits. Use **Send test** in a channel's menu to check it.

**Storage and speed**

- **Song cache**: **Cache played songs** (on by default), **Maximum audio cache (GiB)** (2 GiB by default) and **Keep cached songs for (hours)** (72 by default).
- **Speed**: **Provider streams per listener** (6 by default), **Concurrent media conversions** (2 by default) and **Search wait for music services (ms)**.
- **Advanced: cache times, file checks and cleanup** holds cache times, **Saved-file verification** and cache cleanup.

These limits apply to new work without a restart. When a limit is reached, new service requests get a busy answer instead of waiting forever; music from your library is not affected. See [audio capacity and cleanup](operations/storage.md).

After copying a stopped installation's whole data folder, **Saved-file verification** shows progress checking the kept and downloaded files, and lets you retry. Follow it in **Activity → Everyone → Jobs**. Changed or missing files stay unavailable; verification never replaces their contents.

**Backups and health**

See [Backups and health](#backups-and-health).

**Advanced**

- **Look up songs on your other services** asks link services such as song.link where else a song lives, so playback and lyrics can use your other services. It is off by default. Optional **MusicLink API key** and **song.link API key** sit with it.
- **Install extensions from outside your home network** is off by default.
- **Challenge solver URL** points to your own FlareSolverr or Byparr service, for example `http://flaresolverr:8191`, for sources behind Cloudflare checks. **Test** checks it. **Solver timeout** defaults to 60 seconds.
- **Music server** shows the media-server type. It is set by the deployment.

The full list of what lives where, and what can still be set from Docker, is in [configuration](operations/configuration.md).

### Adding to your library

Allstarr needs write access to the music folder Jellyfin reads. Set it up in **Settings → Library → Where added songs go**. **Available** means a folder can be chosen; it does not mean new songs go there. When there are several, choose **Add songs here** on the one you want. The chosen folder shows **Adds go here**. With only one accessible folder and no choice saved, Allstarr uses it.

If the container can't reach the folder, mount it once, for example `- /path/to/music:/music` in Compose. The host path must be the music folder on the machine running Allstarr; if Jellyfin runs elsewhere, make that folder reachable from the Allstarr host first. Choose **Set path**, enter the **Path inside Allstarr** (for example `/music`), then **Use this folder**. Allstarr checks it can write there with a temporary file and removes it.

`Promotion__AllowedBase` is optional. When set, approved folders must sit below that absolute directory inside the container. Paths that climb out of it and symbolic links are rejected. Allstarr writes songs only inside approved folders.

The rest of the page is a profile: the **Household default**, which listeners follow, or **Your profile**.

- **Default unit**: **Whole album** or **Just the song**. **Remove kept copy** releases the kept copy once Jellyfin has the song; it is on unless you turn it off.
- **Who adds without approval**: **Admins and trusted listeners**, or **Admins**. Everyone else sends a request, which appears in **To library** for you to **Approve and add** or **Decline…**.
- **How added files are named**: **Autodetect** reads the chosen folder and resets the pattern to what it finds, but only when at least five songs are sampled and all of them match. Otherwise your pattern stays and you choose one yourself. Review the examples, then **Use this naming pattern**. Changing the folder or pattern needs this confirmation again.
- **Quality of added songs** and **Tags and extras** set the lowest quality to add, the hi-res ceiling, lossy fallback, MusicBrainz tags, cover art, synced lyrics and ReplayGain.

With **MusicBrainz tags** on, Allstarr finds the song's release on MusicBrainz by its ISRC, or by its title, artist and length when the service gives no ISRC or MusicBrainz does not know it, and tags the file from it, the way a music tagger does: the MusicBrainz ids, sort names, label, barcode, release country, status and type, original date, and the album's name as MusicBrainz writes it (a service's "- EP" or "- Single" is dropped). The release must agree with the service's album on name, track number and track count. When none does, or MusicBrainz doesn't answer within a few seconds, the service's own tags are used.

An added file gets the song's real cover or none; a service's logo is never written as one. **Embedded cover size** sets how large the copy inside the file may be: 1,000 pixels a side unless you change it, or **Full size** to keep the service's own. `cover.jpg`, when you ask for one, is always full size.

Lyrics inside a song file go in twice. The tag every player and media server reads as lyrics holds them line by line, the form all of them understand. Word-by-word timing goes in a tag of its own (`WORDLYRICS`), which Allstarr reads back from the file for its own apps, and in the `.lrc` file when you ask for one. Word times are kept out of the usual tag because a media server that meets them there serves the lines mangled (Jellyfin drops the spaces between words).

A song you deleted from your media server can be added again. The new file takes over the plays and matches of the one that is gone; the heart on the deleted copy goes with it, so heart the song again.

Before adding, Allstarr checks the destination again:

- If a matching album folder already has a different year, choose the existing year or the source's year. Only the folder name changes; the tags don't.
- If the existing album's layout doesn't fit the chosen pattern, adding stops until you choose a pattern that fits.
- A song missing a year or track number that the pattern uses can't be added. Allstarr first fetches the release's details; if they're still missing, pick a release with complete details or a pattern without those fields. It never drops the year or uses track `00` silently.

Kept songs stay playable and downloadable while adding to the library isn't set up. Existing kept copies keep their file names; naming changes apply to new copies. Ownership and safety rules are in [adding songs to the backend library](operations/storage.md).

### Cached and kept songs

- **Cached** songs are temporary copies of songs played from services. They are kept for 72 hours after their last play by default, and the size limit can remove them sooner. Allstarr can fetch them again. A song appears in Cached once its download finishes, even if you skipped it.
- **Kept** songs are permanent copies: from hearts, from playlists with **Keep downloaded copies**, or because someone chose **Keep**. Cache cleanup never removes them.

In **Library → Cached**, choose **Keep** on a song to move it to Kept without downloading again. **Cache settings** opens **Settings → Storage and speed**.

In **Library → Kept**, **Add to library** starts an addition; choose **Only this song** or **Add album to library**. **Stop keeping** removes only your own Keep. Hearts, playlists and other listeners' Keeps still protect the file; when the last one goes, the 24-hour grace period starts. **Delete kept copy…** removes it for everyone.

Neither folder is in the database backup. Back up kept and downloaded music yourself; see [storage operations](operations/storage.md).

### Backups and health

**Backups.** **Settings → Backups and health** shows the last verified backup, its size and the next run. Daily backups are on by default at **03:00 UTC**, keeping 14 daily, 8 weekly and 10 manual copies. Change **Backup time (UTC)**, **Daily copies**, **Weekly copies**, the **Backup folder** and **Manual copies** in **Backup schedule**. **Back up now** makes a snapshot in the background; wait for it to finish before you **Download** it.

A backup holds the database and encryption keys, so keep downloaded ZIP files private. Audio, kept songs, the track cache and installed extensions are not included; back them up separately. A different backup folder must already exist inside the container; mount synchronized storage first.

To restore, choose **Restore…** on a saved backup or **Restore from a file**, then type `RESTORE`. **Back up the current state first** is offered. The restore takes effect when Allstarr next restarts; it doesn't restart on its own. Details are in [storage operations](operations/storage.md).

**Health checks.** Scheduled checks are off until you turn on **Enable scheduled checks**. They run every 30 minutes by default; **Run now** works with scheduling off.

- On Jellyfin, checks use the server API key saved during setup and are read-only: server connection, library search, song details, an audio sample, seeking, artwork and lyrics. They don't create sessions, favorites or playlists.
- On Subsonic, checks need a dedicated non-administrator **Health listener**. Saving marks that listener as a test listener for good: its plays are left out of history, mixes and scrobbles.

An optional **Webhook URL** gets an alert when the same step fails twice in a row, and a message when it passes again. App sign-in and service checks are separate: service checks are in **Sources**.

Under **Advanced: media and playlist tests, request logging**, **Test media pipeline** and **Test playlist readiness** run one-off checks.

### Problem reports

**Activity → Reports** collects listeners' reports. Filter **New**, **Seen**, **Fixed** or **All**. Opening a report doesn't change its status; pick a **Status** and **Save status**, or **Mark fixed**. **Review match** and **Review lyrics** open the song's match or lyrics when the report has them. Saving a match from a new report marks it **Seen**.

## Safety and recovery

- Keep the dashboard on a trusted network or behind an authenticated proxy. See [dashboard access](operations/webui-access.md).
- Use a separate service account where a service allows it, and share accounts with the smallest audience.
- Review previews before imports, restores and bulk removals.
- Make and download a verified backup in **Settings → Backups and health** before an update.
- Back up your configuration, kept music, extension packages and optional service sign-ins separately; see [storage operations](operations/storage.md).

For exact procedures, see [configuration](operations/configuration.md), [storage operations](operations/storage.md) and [client compatibility](operations/client-compatibility.md).
