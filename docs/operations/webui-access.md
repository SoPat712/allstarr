# HTTPS, private dashboard access and SSO

The dashboard uses port 5275 and remains loopback-only by default. URL prefixes and OpenID Connect (OIDC) login are opt-in. Music clients keep using port 5274 with their media-server credentials.

Password checks for dashboard and Jellyfin sign-in, initial setup, and playlist reauthentication share limits of 20 requests per minute for each client address and 60 requests per minute across all those actions. The limits use the address after configured forwarded-header trust, reject without queueing, and return HTTP 429 with a positive `Retry-After` value. OIDC completion callbacks and ordinary music browsing, playback, and seeking remain outside this sign-in budget.

Dashboard sessions expire after 12 hours, or 30 days when **Remember me** is selected for native sign-in. Allstarr rechecks the media-server account when validating a dashboard session. Disabling the account, revoking its backend credentials, or changing its administrator role requires a new sign-in. If the media server is temporarily unavailable, protected requests return HTTP 503 and preserve the existing session cookie. Retry after the media server recovers.

## Reverse-proxy path

For `https://media.example.org/allstarr/`, set:

```dotenv
ADMIN_BASE_PATH=/allstarr
```

Recreate the container after changing this value. Prefixes contain slash-separated letters, digits, hyphens, or underscores. Leave the value empty to serve the dashboard at `/`.

Your proxy may preserve `/allstarr/` or strip it before forwarding to port 5275. Allstarr applies the configured prefix to assets, API requests, live updates, downloads, and session cookies. It ignores `X-Forwarded-Prefix`; a request cannot choose its own mount point. Redirect `/allstarr` to `/allstarr/` at the proxy if it strips the prefix.

An nginx location that strips the prefix looks like this:

```nginx
location = /allstarr { return 308 /allstarr/; }
location /allstarr/ {
    proxy_pass http://allstarr:5275/;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_buffering off;
}
```

This location does not add authentication. Protect the entire path with your access proxy or private network, including API, asset, and OIDC callback routes. Configure the existing admin bind/allowlist policy for your proxy network; enabling a prefix does not open the listener. Trust forwarded headers only from your proxy, using `ForwardedHeaders__KnownProxies` or `ForwardedHeaders__KnownNetworks` in the container environment. Allstarr uses the trusted request scheme for secure session cookies.

A path is not a browser security boundary. Other applications on `media.example.org` share its origin and can make same-origin requests to Allstarr. Use a dedicated hostname when possible; otherwise, only share the hostname with applications you trust. Cookie paths and an access-proxy login do not remove this risk.

Admin responses include `Content-Security-Policy: frame-ancestors 'none'`, `X-Frame-Options: DENY`, and `X-Content-Type-Options: nosniff`. Open the dashboard in its own browser tab or window. These protections cover pages, assets, redirects, and API responses on the admin listener. The generated script policy remains in place when a URL prefix is used. Configure HTTPS at the reverse proxy for remote access.

## Music clients over HTTPS

Use a dedicated HTTPS hostname for remote music clients and forward it to the music listener. On a shared Docker network the upstream is `http://allstarr:8080`; from a proxy running on the Docker host it is normally `http://127.0.0.1:5274`. Keep port 5275 behind the administrator network or access proxy described above. Dashboard SSO does not replace music-client authentication.

Preserve the complete request URI, query parameters, authorization headers, `Range` and `If-Range`. Some clients authenticate downloads using only an `api_key` query parameter. Disable shared caching and response buffering for this upstream, and preserve streaming responses and HTTP 206 headers. See the [nginx proxy directives](https://nginx.org/en/docs/http/ngx_http_proxy_module.html) for these controls. Configure WebSocket forwarding as well if your client uses it.

Query strings can contain credentials. Proxy access logs must exclude them, authorization headers and cookies. The [default nginx access-log format](https://nginx.org/en/docs/http/ngx_http_log_module.html#log_format) includes the original request line; use a sanitized format or disable access logging for the music location. Allstarr's own redaction cannot protect logs written by another service.

Trust forwarded addresses and schemes only from the actual proxy, using the configured known proxies or networks. Do not trust arbitrary client-supplied forwarding headers. Keep **Allow remote extension installation** off unless you deliberately need installation from outside trusted administrator networks; enabling HTTPS does not require enabling remote installation.

Before inviting listeners, verify sign-in, ordinary playback and a seek well into a track through the external HTTPS address. A successful dashboard page load does not verify music-client requests or proxy range handling. Protect the data directory and its backups together: the database, encryption key ring and browser data-protection keys form the recoverable installation.

URLs in extension diagnostics lose account details, query contents and fragments before new entries are stored. The log and activity APIs apply the same redaction when reading older entries. Static failure reasons and response statuses remain available.

### Caddy example

These examples put only the music listener on a public hostname. Run the proxy on the Docker host and keep the dashboard private. Replace the hostname with your own DNS name, point it at the proxy, and allow HTTPS through your firewall. For Caddy's usual certificate challenge, allow port 80 as well.

```caddyfile
music.example.org {
    reverse_proxy 127.0.0.1:5274 {
        flush_interval -1
    }
}
```

Caddy manages HTTPS certificates and forwards WebSockets automatically. Leave access logging disabled for this site, or configure redaction before enabling it. See the [Caddy reverse-proxy reference](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).

### nginx example

Place this inside nginx's `http` configuration. Supply a valid certificate and its private key using your certificate manager. The example redirects HTTP to HTTPS without copying the credential-bearing query into the redirect; configure clients with the HTTPS address from the start.

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name music.example.org;
    access_log off;
    return 308 https://music.example.org$uri;
}

server {
    listen 443 ssl;
    server_name music.example.org;
    ssl_certificate /etc/letsencrypt/live/music.example.org/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/music.example.org/privkey.pem;
    access_log off;

    location / {
        proxy_pass http://127.0.0.1:5274;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
    }
}
```

The upstream has no URI suffix, so the original path and query reach Allstarr. Range and authorization headers pass through by default. Review inherited error logging too: nginx error messages can contain the request URI. Protect and limit those logs, and do not enable debug logging with real accounts. See [nginx WebSocket forwarding](https://nginx.org/en/docs/http/websocket.html).

### Keep administration private

For a proxy on the Docker host, set these deployment values in `.env`. The music listener normally binds to all host interfaces; this override restricts it to the local proxy. The dashboard values below are already the defaults.

```dotenv
PROXY_BIND_ADDRESS=127.0.0.1
ADMIN_BIND_ADDRESS=127.0.0.1
ADMIN_BIND_ANY_IP=false
```

Recreate Allstarr after changing deployment values. Do not forward ports 5274 or 5275 from the router; publish only the HTTPS proxy. Reach the dashboard locally or through an SSH tunnel to the host's loopback port. To allow direct LAN dashboard access instead, set `ADMIN_BIND_ADDRESS` to the host's LAN address, set `ADMIN_BIND_ANY_IP=true` and `ADMIN_TRUSTED_SUBNETS` to your actual LAN CIDR (for example `192.168.1.0/24`), and restrict port 5275 to that LAN in the host firewall. Do not add a public proxy route to port 5275.

If the proxy runs in Docker, attach it to Allstarr's private Docker network and use `allstarr:8080` for music. Host loopback addresses inside another container refer to that container. Keep the admin port unpublished outside the LAN.

Set `ForwardedHeaders__KnownProxies` in Allstarr's Compose `environment` to the exact address Allstarr sees for the proxy (often the Docker bridge gateway for a host proxy). Use `ForwardedHeaders__KnownNetworks` only for a dedicated, controlled proxy subnet. These are container environment settings, not automatically forwarded `.env` entries. Confirm the address for your topology rather than trusting every private network. The edge proxy must overwrite incoming forwarding headers; the examples above do this. This preserves each client's sign-in budget and the original HTTPS scheme.

### Defaults to review before inviting listeners

| Setting | Shipped behavior |
| --- | --- |
| Dashboard binding | Host loopback by default; broader access requires explicit binding and trusted subnets. |
| Dashboard session lifetime | 12 hours; native **Remember me** uses 30 days. OIDC uses 12 hours. These lifetimes are fixed, not dashboard settings. |
| Sign-in limits | 20 requests per minute per client address and 60 globally across protected sign-in actions; HTTP 429 includes `Retry-After`. These limits are fixed. |
| Allow remote extension installation | Off by default (`Extensions:AllowRemoteInstall=false`). Keep it off; HTTPS music access does not need it. |
| Music-client address | In **Getting started**, edit **Music-client address** to the HTTPS hostname before copying the setup instructions. This changes the copied guide, not the listener binding. |

Validate with `caddy validate --config /etc/caddy/Caddyfile` or `nginx -t` before reloading your proxy. Then use an outside-LAN connection to check music-client sign-in, provider playback and a far seek, and confirm the dashboard is unreachable there. Allstarr exposes one selected music protocol per deployment; test the protocol your installation uses. These examples do not configure DNS, certificates, firewall rules or an existing proxy for you.

## OIDC login

Register a confidential web client with your identity provider. Enable authorization-code flow with PKCE and register this exact redirect URI:

```text
https://media.example.org/allstarr/api/admin/auth/oidc/callback
```

Keep `ADMIN_BASE_PATH=/allstarr` in deployment configuration when using that prefix. Single sign-on policy and the encrypted client secret are saved in the data folder under **Settings → Network → Single sign-on**, backed by the authenticated administrator access API described in [configuration](configuration.md#administrator-access-settings). Use authority `https://identity.example.org`, client ID `allstarr`, public URL `https://media.example.org/allstarr`, and your own client secret. Set the display name to the name you want on the login screen, then enable and save. Saving requests a restart after the response completes.

Use your provider's issuer/authority URL, which may include a realm or application path. Both authority and public URL require HTTPS. The public URL must end at the configured base path with no query or fragment. For a dedicated hostname with no prefix, leave `ADMIN_BASE_PATH` empty and use `https://allstarr.example.org` as the public URL.

Client secrets are encrypted for the exact identity service and client ID and are never returned by the configuration API. Persist the data-protection keys and encryption key ring across restarts. Changing identity service or client ID requires a new secret. Old environment-based SSO values are left for explicit review in the upgrade preview.

### Link a media-server account

1. Choose **Continue with Home SSO** on the login page.
2. Sign in at the identity provider.
3. Enter your Jellyfin or Subsonic credentials and choose **Link account and sign in**.

Allstarr binds the verified issuer and subject to that exact backend account. Email addresses and IdP role claims do not choose a media account or grant administrator access. One SSO identity links to one native account, and a native account has one SSO link. Disconnect an existing link before choosing another account.

For Jellyfin, Allstarr stores the linked account's access token encrypted. For Subsonic, it stores the supplied native credential encrypted; the linking screen explains this before submission. Ordinary Subsonic login does not retain that credential. On future SSO logins, Allstarr checks the stored credential with the selected media server and reads its current permissions. A changed backend URL, rejected credential, or disabled account prevents SSO login.

SSO creates a normal 12-hour Allstarr session. Signing out ends the local session, not the identity-provider session. Native username/password login remains available if the identity provider is down.

### Disconnect and recover

Use **Disconnect SSO** while signed in to remove your link and revoke its stored credential. Existing SSO-created Allstarr sessions stop working; a native-login session remains usable. If a native credential expires, start SSO again and authenticate the same media account to renew the link.

Disabling single sign-on through administrator access settings and restarting disables SSO and its existing sessions without changing music-client login. A verified database-and-key-ring backup preserves account links and encrypted credentials. Browser session protection keys are separate files, so sign in again after restoring onto a fresh installation. A stopped copy of the complete data folder includes those protection keys.

If callbacks fail, check the exact redirect URI, public URL, proxy path handling, and trusted forwarded-protocol configuration. Do not include authorization codes, cookies, passwords, or tokens in bug reports.
