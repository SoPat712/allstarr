# Apple download provider

Apple downloads are optional. The `apple` profile in `docker-compose.yml` adds Allstarr's Apple gateway, GAMDL
3.9.1, and a locally built, source-locked
wrapper-v2 0.0.2 service. It does not contain or download Apple code.

The URL must point to a gateway that wraps GAMDL and wrapper-v2 and implements the API contract Allstarr expects.
Do not enter the raw wrapper-v2 URL. wrapper-v2 supplies account, playback, and decryption services, but it is not a
GAMDL search and download HTTP gateway by itself.

## Prepare wrapper-v2

Obtain Apple Music for Android legally from a source you are permitted to use. Allstarr does not download or
redistribute that package. Open **Integrations > Services > Apple Music**, choose **Sign in to Apple Music** in step 2, then open **Advanced setup** if the package or gateway is missing. Upload the APK/APKM and run:

```bash
./allstarr.sh enable apple x86_64
```

For ARM64, use `./allstarr.sh enable apple arm64-v8a`. The command sets both wrapper architecture values. To supply a package directly, use `./allstarr.sh enable apple /path/to/package.apkm x86_64`. The lower-level preparation script accepts `--apkm` and `--arch`; the host controller uses positional arguments.

An old installation's archived libraries can be reused only when they pass the official hash lock:

```bash
./allstarr.sh prepare-apple /backup/rootfs/system/lib64 x86_64
```

The controller clones official wrapper-v2 tag `0.0.2`, verifies its full locked commit, stages the official
AOSP runtime, and rejects Apple or AOSP libraries that do not match wrapper-v2's `LIBS_VERSION.json`. The generated
`.apple-provider/wrapper-v2` directory is local deployment input and must not be committed.

## What the profile runs

The repository gateway is a narrow HTTP adapter around the official upstream projects
[GAMDL](https://github.com/glomatico/gamdl) and
[wrapper-v2](https://github.com/glomatico/wrapper-v2). It exposes download-backed streaming,
managed song downloads, health, login, and 2FA. It runs on the private Compose network and publishes no host port.

Apple's public catalog and personal playlists run directly in Allstarr. Connect one **Personal Apple Music** account with its `media-user-token` and two-letter storefront; no developer token is required. Catalog search and lookups use that account's storefront without opening its token; without an account they use `us`. Allstarr fetches and caches the public web-player bearer. A fetch failure appears on the Apple account status card and can be retried with **Test**.

The optional gateway supplies track streaming, managed downloads, and synced lyrics. Each media request uses the selected account's encrypted credential. Cache entries are separated by account revision and storefront. Wrapper-backed audio requires the wrapper and selected account to identify the same Apple account; a mismatch fails before media access. Catalog access works without the optional gateway, and public playlists do not require a user token.

Before connecting it, confirm that:

- the gateway is reachable from the Allstarr container through an explicit HTTP or HTTPS URL;
- its health endpoint reports the expected API and capability manifest;
- its account/session and 2FA flow works without exposing credentials in the URL;
- its output and session paths are persistent and backed up according to the gateway's own runbook; and
- any reverse proxy preserves streaming responses and uses a private, authenticated network boundary.

### Gateway contract

Allstarr first requests `GET /api/capabilities`. A compatible version 2 response looks like this:

```json
{
  "sidecarApiVersion": "2.1.0",
  "capabilities": [
    { "id": "audio-file", "state": "supported" },
    { "id": "stream-audio-song", "state": "supported" },
    { "id": "download-audio-song", "state": "supported" },
    { "id": "synced-lyrics-artifact", "state": "supported" }
  ]
}
```

The included gateway also advertises its verified ALAC and AAC input support. Allstarr displays absent features as
unsupported.

Background discovery appears on the Apple Music provider card even before an account Test. A missing gateway session shows **Apple download needs sign-in**; an incompatible gateway shows **Apple download gateway needs an update**. Jellyfin playback reports these setup failures as HTTP 503 with the same explanation, and Activity records the reason. Listener authorization failures remain HTTP 403. A gateway sign-in and a Personal Apple Music account are separate prerequisites.

Gateway API 2 requires the selected Media User Token, storefront, and opaque account-revision scope in headers for every media request, including HEAD. Version 1 gateways are rejected because they cannot guarantee that boundary. Credentials never appear in URLs; request cookie files are private and removed after the subprocess exits.

The current typed lanes use these routes:

| Purpose | Gateway route |
| --- | --- |
| Capability discovery | `GET /api/capabilities` |
| Runtime health | `GET /api/health` |
| Account status | `GET /api/me` |
| Login and 2FA | `POST /api/login`, `POST /api/login/2fa` |
| Managed track artifact | `GET /api/download/{id}?quality={quality}` |
| Complete file playback | `GET /api/audio/{id}?quality={quality}` |
| File headers, preparing if needed | `HEAD /api/audio/{id}?quality={quality}` |
| Legacy progressive playback | `GET /api/stream/{id}?quality={quality}` |
| Synced lyrics artifact | `GET /api/lyrics/{id}` |

When the gateway advertises `audio-file`, playback waits for a complete cached file before responding.
Lossless sources become FLAC with a total sample count and seek table; AAC sources remain MP4.
The file response includes its length and byte-range support, including 206 and 416 responses. Preparation
and conversion are shared across concurrent requests and cached for six idle hours, up to 32 tracks per
gateway. A missing audio artifact returns a structured 502 before any bytes, allowing Allstarr to try the
next provider. Account mismatch returns 403, and process timeout returns 504. Older gateways continue to
use `/api/stream`; managed downloads keep using `/api/download`.

Health must report the gateway, GAMDL runtime, and wrapper dependency truthfully. A raw wrapper-v2 `/health`
response is not enough. Allstarr refuses redirects during discovery and login so credentials cannot be forwarded to
an unexpected host.

## Connect Allstarr

Open **Integrations > Services > Apple Music**. Step 1 connects the Personal account using its Media User Token and storefront. Step 2 signs the gateway into the same Apple account, including 2FA when requested. Step 3 tests the saved account and playback. The sign-in dialog checks setup before showing a form; existing installations go straight to sign-in, and missing package or gateway steps appear under Advanced setup. Successful sign-in closes the dialog and displays a notification. When reported by the gateway, the signed-in identifier is masked. The gateway verifies account identity during media preparation; the dashboard cannot infer a token’s account from its masked identifier.

Set the private gateway URL to `http://apple-gateway:8000` in the Apple service settings for the bundled profile. A blank address does not configure an endpoint. This URL is read-only when an existing deployment manages it. Sign-in credentials are forwarded only to wrapper-v2 and are not stored in the database.

The legacy `APPLE_MUSIC_AIO_URL` name is recognized only by the migration review so an operator can identify the
old endpoint. New deployments save the gateway address in the dashboard.

Apply deployment changes through the saved profile:

```bash
./allstarr.sh up
./allstarr.sh status
```

`up` rebuilds the gateway from the local files whenever the Apple profile is enabled, when using the normal controller. The developer script does the same for source builds. A failed build stops startup.

For later application and gateway updates, use `./allstarr.sh update`. It preserves the Apple profile and its
session volumes. Run `prepare-apple` again only when the verified wrapper inputs need to change; use
`install-apple` when you want that verification, build, and startup handled in one command.

Use the dashboard to check the provider state, finish login or 2FA if the compatible gateway supports that flow,
and test the capabilities you intend to route. A reachable container is not enough. Allstarr should select only the
capabilities reported by the gateway and accepted by its health and compatibility checks.

Optional media uses the selected Personal Apple Music account through a protected request. The gateway creates a temporary private cookie file and removes it after the request. Its wrapper session must belong to the same Apple account; a mismatch stops media preparation. Do not configure a global cookie fallback.

## Disable or replace the gateway

Run `./allstarr.sh disable apple`. This stops and removes the two optional containers and forgets the saved profile. It does not delete SQLite records, Allstarr-managed media, the wrapper session volume, gateway
state, or a user's Personal Apple Music account.

To replace the gateway, validate the replacement independently, change the URL in the dashboard, and repeat the
provider health and capability checks. Do not combine that change with a database restore or media move. Keeping
those operations separate makes rollback much easier.
