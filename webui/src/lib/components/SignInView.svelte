<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { adminUrl } from "$lib/admin-url";
  import type { OidcStatus } from "$lib/admin-oidc";
  let { backend, oidc, username = $bindable(""), password = $bindable(""), rememberMe = $bindable(true), managePlaylists = $bindable(false), busy = false, error = "", onlogin, oncancel }: {
    backend: string; oidc: OidcStatus; username?: string; password?: string; rememberMe?: boolean; managePlaylists?: boolean; busy?: boolean; error?: string;
    onlogin: () => Promise<void>; oncancel: () => Promise<void>;
  } = $props();
</script>
  <main class="grid min-h-screen place-items-center p-6">
    <section class="panel sign-in-card w-full max-w-sm p-6 sm:p-8" aria-labelledby="login-title">
      <div class="sign-in-brand"><span class="brand-mark login-brand" aria-hidden="true">A</span><strong>Allstarr</strong></div>
      <h1 id="login-title" class="mt-2 text-3xl font-semibold tracking-tight">Sign in</h1>
      <p class="mt-3 text-sm leading-6 text-ink-muted">
        {#if oidc.linkPending}
          Link your {backend} account to SSO. Your media server still controls your access.
          {#if backend.toLowerCase() === "subsonic"}
            Allstarr will keep your media-server credential encrypted to verify future SSO logins.
          {/if}
        {:else}
          Use your {backend} account.
        {/if}
      </p>

      <form class="mt-8 space-y-4" aria-busy={busy} onsubmit={(event) => { event.preventDefault(); void onlogin(); }}>
        <label class="field">
          <span>Username</span>
          <input bind:value={username} autocomplete="username" required disabled={busy} />
        </label>
        <label class="field">
          <span>Password</span>
          <input bind:value={password} type="password" autocomplete="current-password" required disabled={busy} />
        </label>
        <label class="flex min-h-11 items-center gap-3 text-sm text-ink-muted">
          <input bind:checked={rememberMe} type="checkbox" class="size-4 accent-signal" disabled={busy} />
          Keep me signed in
        </label>
        {#if backend.toLowerCase() === "subsonic"}
          <label class="flex min-h-11 items-center gap-3 text-sm text-ink-muted">
            <input bind:checked={managePlaylists} type="checkbox" class="size-4 accent-signal" disabled={busy} />
            Let Allstarr manage my playlists
          </label>
        {/if}
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        <button class="auth-submit w-full" type="submit" disabled={busy}>{busy ? "Signing in…" : oidc.linkPending ? "Link account and sign in" : "Sign in"}</button>
        {#if oidc.enabled && !oidc.linkPending && oidc.loginUrl}
          <div class="sign-in-divider">or</div><Button variant="outline" class="w-full" href={adminUrl(oidc.loginUrl)}>Continue with {oidc.displayName}</Button>
        {/if}
        {#if oidc.linkPending}
          <Button variant="outline" class="w-full" type="button" disabled={busy} onclick={() => void oncancel()}>Continue without linking</Button>
        {/if}
      </form>
    </section>
  </main>
<style>
  .sign-in-card { max-width: 400px; }
  .sign-in-brand { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-6); font-size: 1.25rem; }
  .sign-in-divider { display: flex; align-items: center; gap: var(--space-3); color: var(--color-ink-muted); font-size: var(--text-sm); }
  .sign-in-divider::before, .sign-in-divider::after { content: ""; height: 1px; flex: 1; background: var(--color-edge); }
</style>
