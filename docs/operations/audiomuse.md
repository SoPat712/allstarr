# AudioMuse connection

AudioMuse is optional and disabled until an administrator sets its URL. Core startup and saved playlists do not contact it. Finish setup on an existing upstream v3.6.4 installation separately.

Open **Intelligence → Sounds → Edit connection** as an administrator and enter the server address and API key. A blank key field preserves the saved key; clear the server address to disable the connection. Listeners can search analyzed seeds, preview similar songs or Calm/Workout mixes, and save private playlists from Sounds.

For direct administration on Allstarr's admin port, use an authenticated administrator session with `GET /api/admin/audiomuse/connection`, then `PUT` the same route with JSON containing `baseUrl`, `apiKey`, and the returned `revision` (initially null). Example URL: `http://<host>:8600`. Enter the upstream `API_TOKEN` as `apiKey`; requests use Bearer authentication. The key is encrypted and never returned. Omit `apiKey` to retain it, send an empty string to clear it, and set `baseUrl` to an empty string to disable the connection. Changes apply immediately.

A single Jellyfin entry in the upstream registry binds directly. With multiple Jellyfin entries, exactly one must match Allstarr's configured Jellyfin URL, including port and base path. Health alone does not establish readiness; authenticated version, registry and model coverage must qualify.

Signed-in listeners on the admin port can use:

- `GET /api/admin/audiomuse/status`: the bound AudioMuse server's local musicnn analyzed count alongside the listener's accessible library total. `coverageComplete=true` means AudioMuse has analyzed its whole indexed subset; that subset can be smaller than the library.
- `GET /api/admin/audiomuse/seeds?query=…`: analyzed library song choices.
- `POST /api/admin/audiomuse/similar` with `seedId` and optional `n` (default 25, maximum 50).
- `POST /api/admin/audiomuse/mood` with `mood` (`calm` or `workout`) and optional `n`.
- `POST /api/admin/audiomuse/save` with `previewId` and `name`: create an ordinary private Jellyfin playlist. Permissions and clean-only policy are checked again, including when AudioMuse has since been disabled.

Previews expire after 30 minutes. Successful save retries return the stored playlist ID. An interruption between native creation and receipt persistence can make a retry create a duplicate.

Library files and already indexed kept files participate under native Jellyfin IDs after analysis. Results exclude external songs, seed copies and proven duplicates, with an artist cap of three. `eligibleCount` covers the bounded candidate pool before artist limiting. Recipes are fixed at `calm-v1` and `workout-v1`; missing analysis is never filled with external music. Unavailable results include a reason. Status and previews have a twenty-second overall budget. No analysis, history submission or background work is triggered. Available moods, features and top genre from permitted discovery results are retained as shared song tags.
