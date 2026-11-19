# Apple download provider

Apple Music catalog search, lookups and playlists run inside Allstarr. Playback, downloads and synced lyrics need the optional Apple gateway. The `apple` profile in `docker-compose.yml` adds Allstarr's gateway, which wraps GAMDL 3.9.1, and a locally built, source-locked wrapper-v2 0.0.2 service. The repository contains no Apple code and downloads none.

## Connect an Apple Music account

In **Sources → Accounts**, connect an Apple Music account with its **Media User Token** and two-letter storefront. Use a Personal account for one listener or a Shared account for the household. No developer token is needed.

Catalog search and lookups use the account's storefront; without an account they use `us`. Allstarr fetches and caches Apple's public web-player token itself. If that fails, the Apple Music account shows the error, and **Test** tries again. Public playlists need no user token.

## Set up the gateway

The **Apple Music setup** card in **Sources → Services → Apple Music** has three steps. Use the same Apple account for steps 1 and 2.

1. **Apple Music account**: the account above.
2. **Apple Music sign-in for playback**: signs the gateway in to Apple, for streaming, downloads and synced lyrics.
3. **Test playback**: checks the saved account and measures how quickly its audio starts.

### Build the gateway

Get Apple Music for Android legally, from a source you are allowed to use. Allstarr does not download or redistribute it. Choose **Sign in to Apple Music**, open **Advanced setup** and upload the APK or APKM. Then run on the Docker host:

```bash
./allstarr.sh enable apple
```

The architecture is detected automatically. Pass `x86_64` or `arm64-v8a` when needed; the command saves the matching wrapper settings in `.env`. To use a package file directly, run `./allstarr.sh enable apple /path/to/package.apkm x86_64`.

Libraries archived from an older installation can be reused only when they match the official hash lock:

```bash
./allstarr.sh prepare-apple /backup/rootfs/system/lib64 x86_64
```

The script clones the official wrapper-v2 tag `0.0.2`, checks its full locked commit, stages the official AOSP runtime, and rejects Apple or AOSP libraries that don't match wrapper-v2's `LIBS_VERSION.json`. The generated `.apple-provider/wrapper-v2` folder is local deployment input; never commit it. `prepare-apple` verifies and builds without starting; `enable apple` does all of it and starts the profile. `install-apple` is an older name for `enable apple`. See [deployment](deployment-profiles.md#optional-apple-downloads) for staging paths.

### Point Allstarr at the gateway

On the Apple Music service, set **Optional download gateway URL** to `http://apple-gateway:8000` for the bundled profile. A blank address means no gateway. This is a dashboard setting, not a container variable; `APPLE_DOWNLOAD_URL` is its name in a version 2 `.env`, and the [legacy import](legacy-env-import.md) brings it over. The even older `APPLE_MUSIC_AIO_URL` is only reported by the import so you can find the old address.

Enter the gateway's address, never the raw wrapper-v2 address. wrapper-v2 provides account, playback and decryption services, but it is not a search and download gateway by itself.

## Connect Allstarr

Choose **Sign in to Apple Music** and sign the gateway in to the same Apple account, including two-factor authentication when asked. The dialog checks setup first: an existing installation goes straight to sign-in, and missing package or gateway steps appear under **Advanced setup**. When the gateway reports the signed-in account, its identifier is shown masked. Sign-in credentials go only to wrapper-v2 and are not stored in Allstarr's database.

Then use **Test playback**. A running container is not enough; Allstarr uses only the capabilities the gateway reports and that pass its health and compatibility checks.

Each media request uses the selected account's encrypted Media User Token. The gateway writes it to a private temporary cookie file and removes it after the request. Its wrapper session must belong to the same Apple account; a mismatch stops before any media is prepared. Don't set up a global cookie fallback. Caches are kept apart by account revision and storefront.

Before you rely on it, check that:

- the gateway is reachable from the Allstarr container at an explicit HTTP or HTTPS address;
- its health endpoint reports the expected API and capabilities;
- sign-in and two-factor authentication work without credentials in the URL;
- its output and session folders are persistent and backed up (see the [gateway guide](../../sidecars/apple-gateway/README.md)); and
- any reverse proxy in between passes streaming responses through and sits on a private, authenticated network.

Background checks show gateway problems on the Apple Music service even before a Test. A gateway without a session shows **Apple download needs sign-in**; an incompatible one shows **Apple download gateway needs an update**. Jellyfin playback returns these setup problems as HTTP 503 with the same text, and Activity records the reason. A listener without permission gets HTTP 403. Gateway sign-in and an Apple Music account are separate requirements.

## Playback

When the gateway advertises `audio-file`, Allstarr plays through `/api/audio`. An uncached song streams as the gateway prepares it: lossless sources as FLAC, AAC sources as fragmented MP4. That first response has no length or byte-range support; the gateway sends the catalog duration and Allstarr adds duration guidance. Listeners asking for the same song, account and quality share one preparation.

When preparation finishes, the gateway caches the complete file for six idle hours, up to 32 songs. Cached responses include their length and support byte ranges, with 206 and 416 responses. A HEAD request prepares the whole file before it answers.

A failure before the first audio bytes returns a structured error: missing audio gives 502, so Allstarr can try the next service; an account mismatch gives 403; a process timeout gives 504. A failure after audio has started ends the transfer, and Allstarr does not cache the partial file. Older gateways without `audio-file` keep using `/api/stream`. Managed downloads always use `/api/download`.

Original downloads can keep Apple's ALAC M4A instead of converting it to FLAC. See [client compatibility](client-compatibility.md#seeking-ranges-and-conversion) for how seeking and caching work.

## Gateway contract

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

The bundled gateway also advertises the ALAC and AAC input it supports. Allstarr shows missing features as unsupported.

Gateway API 2 needs the selected Media User Token, storefront and an opaque account-revision scope in headers on every media request, including HEAD. Version 1 gateways are refused because they can't guarantee that. Credentials never appear in URLs. Health must honestly report the gateway, GAMDL and wrapper; a raw wrapper-v2 `/health` answer is not enough. Allstarr refuses redirects during discovery and sign-in, so credentials can't be sent to an unexpected host.

| Purpose | Gateway route |
| --- | --- |
| Capability discovery | `GET /api/capabilities` |
| Runtime health | `GET /api/health` |
| Account status | `GET /api/me` |
| Sign-in and two-factor authentication | `POST /api/login`, `POST /api/login/2fa` |
| Managed song download | `GET /api/download/{id}?quality={quality}` |
| Playback | `GET /api/audio/{id}?quality={quality}` |
| File headers, preparing if needed | `HEAD /api/audio/{id}?quality={quality}` |
| Older progressive playback | `GET /api/stream/{id}?quality={quality}` |
| Synced lyrics | `GET /api/lyrics/{id}` |

## Update the gateway

`./allstarr.sh up` rebuilds the gateway from the local files whenever the Apple profile is saved, and a failed build stops startup. `./allstarr.sh update` updates Allstarr and the gateway and keeps the Apple profile and its session volumes. The developer script does the same for source builds. Run `prepare-apple` again only when the verified wrapper inputs need to change.

```bash
./allstarr.sh up
./allstarr.sh status
```

## Disable or replace the gateway

Run `./allstarr.sh disable apple`. It stops and removes the two Apple containers and forgets the saved profile. It keeps Allstarr's records, its music, the wrapper session volume, gateway data and everyone's Apple Music accounts.

To replace the gateway, test the new one on its own, change the URL in the dashboard, and repeat the health and capability checks. Don't combine that with a database restore or a media move; keeping them separate makes rollback much easier.
