# Extension session verifier

This optional service opens the approved `api.zarz.moe` session challenge in
Chromium and clicks its native Cloudflare Turnstile checkbox. The challenge page
performs its own verification request. The service returns that request's grant
as a callback to Allstarr, which exchanges it using the existing installation
identity and saves the resulting session in encrypted storage.

Enable it from the deployment directory:

```bash
./allstarr.sh enable sessions
```

The script builds the image, saves the optional `sessions` profile, and recreates
Allstarr with the verifier's internal address. Source deployments use the same
command. Subsequent `up` and `update` commands preserve the profile. Disabling it
retains saved connections and restores manual verification:

```bash
./allstarr.sh disable sessions
```

For direct Compose use, set `SESSION_VERIFIER_URL=http://session-verifier:8000`
and pass `--profile sessions`. Other deployments can set
`Extensions__SessionVerification__BaseUrl` to the private service address.

Allstarr queues verification only for a previously signed-in connection that
has expired or whose renewal is capped within the next hour. Initial sign-in and
manually started verification remain under the administrator's control. Work is
durable, bound to the exact package and connection revision, and limited to
three attempts with five-minute retry delays. Restart recovery rechecks the
connection. Sign-out, removal, account changes, package changes, and cancellation
discard an in-flight result. Activity shows the outcome; **Reconnect** remains
available if the service cannot complete verification.

The container has no published port, durable volume, provider credentials,
application data mount, or Docker socket. It runs as an unprivileged user with a
read-only root filesystem and bounded memory/process counts. Chromium uses a
temporary profile and an internal virtual display; its sandbox is disabled
inside this restricted container. Browser requests are limited to
`api.zarz.moe` and `challenges.cloudflare.com`. Only a `grant` from the exact
challenge verification endpoint is accepted. Tokens, cookies, profiles, and raw
responses are neither persisted nor logged. A parent process deadline kills the
browser process group if it stalls.

The protocol follows the browser-based session flow in
[SpotiFLAC-Module-Version](https://github.com/BartolomeoRusso9/SpotiFLAC-Module-Version/blob/main/SpotiFLAC/core/signed_session_mobile.py).
The image pins `nodriver` and its dependencies in `uv.lock`; it does not load
SpotiFLAC extensions or run a second music provider runtime.

Run deterministic contracts without provider credentials:

```bash
PYTHONPATH=sidecars/session-verifier python3 -m unittest discover -s sidecars/session-verifier/tests -v
```

From this directory, use `python3 -m unittest discover -s tests -v`. The separate
`tools/smoke_browser.py` fixture checks real Chromium/CDP startup inside the image
without opening a provider page.
