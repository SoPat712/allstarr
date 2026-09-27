# WebUI proxy and SSO setup

The dashboard uses port 5275 and remains loopback-only by default. URL prefixes and OpenID Connect (OIDC) login are opt-in. Music clients keep using port 5274 with their media-server credentials.

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
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_buffering off;
}
```

This location does not add authentication. Protect the entire path with your access proxy or private network, including API, asset, and OIDC callback routes. Configure the existing admin bind/allowlist policy for your proxy network; enabling a prefix does not open the listener. Trust forwarded headers only from your proxy, using `ForwardedHeaders__KnownProxies` or `ForwardedHeaders__KnownNetworks` in the container environment. Allstarr uses the trusted request scheme for secure session cookies.

A path is not a browser security boundary. Other applications on `media.example.org` share its origin and can make same-origin requests to Allstarr. Use a dedicated hostname when possible; otherwise, only share the hostname with applications you trust. Cookie paths and an access-proxy login do not remove this risk.

## OIDC login

Register a confidential web client with your identity provider. Enable authorization-code flow with PKCE and register this exact redirect URI:

```text
https://media.example.org/allstarr/api/admin/auth/oidc/callback
```

Configure Allstarr:

```dotenv
ADMIN_BASE_PATH=/allstarr
ADMIN_OIDC_ENABLED=true
ADMIN_OIDC_AUTHORITY=https://identity.example.org
ADMIN_OIDC_CLIENT_ID=allstarr
ADMIN_OIDC_CLIENT_SECRET=replace-with-your-client-secret
ADMIN_OIDC_PUBLIC_URL=https://media.example.org/allstarr
ADMIN_OIDC_DISPLAY_NAME=Home SSO
```

Use your provider's issuer/authority URL, which may include a realm or application path. Both authority and public URL require HTTPS. The public URL must end at the configured base path with no query or fragment. For a dedicated hostname with no prefix, leave `ADMIN_BASE_PATH` empty and use `https://allstarr.example.org` as the public URL.

Keep the client secret in private deployment configuration, not Git or dashboard settings. The Compose deployment reads these keys from its mounted `.env`; direct deployments can use the corresponding `Admin__Oidc__...` configuration keys. Persist the existing data-protection keys and encryption key ring across restarts.

### Link a media-server account

1. Choose **Continue with Home SSO** on the login page.
2. Sign in at the identity provider.
3. Enter your Jellyfin or Subsonic credentials and choose **Link account and sign in**.

Allstarr binds the verified issuer and subject to that exact backend account. Email addresses and IdP role claims do not choose a media account or grant administrator access. One SSO identity links to one native account, and a native account has one SSO link. Disconnect an existing link before choosing another account.

For Jellyfin, Allstarr stores the linked account's access token encrypted. For Subsonic, it stores the supplied native credential encrypted; the linking screen explains this before submission. Ordinary Subsonic login does not retain that credential. On future SSO logins, Allstarr checks the stored credential with the selected media server and reads its current permissions. A changed backend URL, rejected credential, or disabled account prevents SSO login.

SSO creates a normal 12-hour Allstarr session. Signing out ends the local session, not the identity-provider session. Native username/password login remains available if the identity provider is down.

### Disconnect and recover

Use **Disconnect SSO** while signed in to remove your link and revoke its stored credential. Existing SSO-created Allstarr sessions stop working; a native-login session remains usable. If a native credential expires, start SSO again and authenticate the same media account to renew the link.

Setting `ADMIN_OIDC_ENABLED=false` and recreating the container disables SSO and its existing sessions without changing music-client login. A full PostgreSQL backup preserves links. Portable and selective state exports exclude SSO links and their credentials; link accounts again after a portable restore.

If callbacks fail, check the exact redirect URI, public URL, proxy path handling, and trusted forwarded-protocol configuration. Do not include authorization codes, cookies, passwords, or tokens in bug reports.
