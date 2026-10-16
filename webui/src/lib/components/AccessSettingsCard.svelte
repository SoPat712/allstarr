<script lang="ts">
  import ServiceAccessRestore from "$lib/components/ServiceAccessRestore.svelte";
  import { onMount } from "svelte";
  import { setupAccess, type OidcAccess, type BackendAccess } from "$lib/api";
  import { consumeAccessRestored, markAccessRestored, savedBeforeRestart, waitForRestart } from "$lib/restart";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  let { kind }: { kind: "oidc" | "backend" } = $props();
  let snapshot = $state<OidcAccess | BackendAccess | null>(null), csrf = $state("");
  let enabled = $state(false), authority = $state(""), clientId = $state(""), publicUrl = $state(""), displayName = $state("Single sign-on");
  let secret = $state(""), removeSecret = $state(false), username = $state(""), password = $state("");
  let busy = $state(false), error = $state(""), saved = $state(false), editing = $state(false), restored = $state(false);
  const abort = new AbortController();
  const oidc = $derived(kind === "oidc" ? snapshot as OidcAccess | null : null);
  const backend = $derived(kind === "backend" ? snapshot as BackendAccess | null : null);
  const connected = $derived(!!backend?.configured && !backend.needsReview);
  async function load() {
    error = ""; editing = false;
    try {
      const response = kind === "oidc" ? await setupAccess.oidc() : await setupAccess.backend();
      snapshot = response.settings; csrf = response.csrfToken;
      if (kind === "oidc") {
        const value = response.settings as OidcAccess;
        enabled = value.enabled; authority = value.authority; clientId = value.clientId;
        publicUrl = value.publicUrl; displayName = value.displayName || "Single sign-on";
      } else enabled = (response.settings as BackendAccess).configured;
      secret = ""; password = ""; removeSecret = false;
    } catch (cause) { error = cause instanceof Error ? cause.message : "Access settings are unavailable. Reload to retry."; }
  }
  async function save() {
    if (!snapshot || busy) return;
    busy = true; error = "";
    try {
      if (!saved) {
        const value = kind === "oidc"
          ? { enabled, authority, clientId, publicUrl, displayName, clientSecret: secret || null, removeClientSecret: removeSecret, expectedRevision: snapshot.revision }
          : { enabled, username, password, expectedRevision: snapshot.revision };
        try {
          await setupAccess.save(kind, value, csrf);
        } catch (cause) { if (!savedBeforeRestart(cause)) throw cause; }
        saved = true;
      }
      secret = ""; password = "";
      await waitForRestart(abort.signal);
      if (kind === "backend" && enabled) markAccessRestored();
      window.location.reload();
    } catch (cause) { error = cause instanceof Error ? cause.message : "Access could not be saved. Reload the settings and retry."; }
    finally { secret = ""; password = ""; busy = false; }
  }
  function cancelEdit() {
    editing = false; enabled = true; username = ""; password = ""; error = "";
  }
  onMount(() => {
    if (kind === "backend") restored = consumeAccessRestored();
    void load();
    return () => abort.abort();
  });
</script>
{#if kind === "backend" && !connected}<ServiceAccessRestore />{/if}
<article class="settings-card access-card p-5 space-y-4" aria-label={kind === "oidc" ? "Single sign-on" : "Media-server access"}>
  <header>
    <h3>{kind === "oidc" ? "Single sign-on" : "Media-server access"}</h3>
    {#if backend?.supported && backend.connectionSaved && !saved}
      <Badge tone={connected ? "success" : "warning"}>{connected ? "Connected" : backend.needsReview ? "Needs attention" : "Off"}</Badge>
    {/if}
  </header>
  {#if kind === "oidc"}
    <p>Connect an OpenID Connect identity provider. Media-server login remains available. Secrets are encrypted and never displayed.</p>
  {:else if !connected || editing}
    <p>Allow unattended Jellyfin playlist sync and library scans with your verified administrator access token. Your password is used only for verification and is discarded. Each listener's library permissions remain authoritative.</p>
  {/if}
  {#if kind === "oidc" && snapshot?.needsReview}<p class="notice-error">The saved connection needs attention. Enter fresh credentials or turn it off.</p>{/if}
  {#if backend && (!backend.supported || !backend.connectionSaved)}<p>{!backend.supported ? "Subsonic access is granted separately by each listener." : "Preview and import the mounted settings to save the current media-server connection first."}</p>
  {:else if backend && connected && !editing && !saved}
    {#if restored}<p class="access-restored" role="status">Media-server access is restored. Playlist sync and library scans are running again.</p>{/if}
    <p>Playlist sync and library scans use encrypted service access. Each listener's library permissions remain authoritative.</p>
    {#if backend.credentialKind === "api-key"}<p>Save administrator credentials to enable full playlist order and removal sync.</p>{/if}
    <Button variant="secondary" onclick={() => { editing = true; restored = false; }}>Change access</Button>
  {:else if snapshot && !saved}
    <form class="access-form" onsubmit={(event) => { event.preventDefault(); void save(); }}>
      <label class="access-toggle"><Checkbox class="access-checkbox" style="min-height: 44px; min-width: 44px" bind:checked={enabled} disabled={busy} /><span>{kind === "oidc" ? "Enable single sign-on" : "Allow playlist sync and library scans"}</span></label>
      {#if kind === "oidc"}
        <label class="field"><span>Issuer address</span><input type="url" bind:value={authority} disabled={busy} required={enabled} /></label>
        <label class="field"><span>Client ID</span><input bind:value={clientId} disabled={busy} required={enabled} /></label>
        <label class="field"><span>Public Allstarr address</span><input type="url" bind:value={publicUrl} disabled={busy} required={enabled} /></label>
        <label class="field"><span>Sign-in button name</span><input bind:value={displayName} disabled={busy} /></label>
        <label class="field"><span>Client secret</span><input type="password" aria-label="Client secret" autocomplete="new-password" bind:value={secret} disabled={busy || removeSecret} /><small>{oidc?.hasClientSecret ? "A secret is saved. Leave blank to keep it for the same issuer and client." : "No secret is saved."}</small></label>
        <label class="access-toggle"><Checkbox class="access-checkbox" style="min-height: 44px; min-width: 44px" bind:checked={removeSecret} disabled={busy} /><span>Remove saved client secret</span></label>
      {:else if enabled}
        <label class="field"><span>Jellyfin administrator username</span><input autocomplete="username" bind:value={username} required disabled={busy} /></label>
        <label class="field"><span>Jellyfin administrator password</span><input type="password" autocomplete="current-password" bind:value={password} required disabled={busy} /></label>
      {:else if connected}
        <p class="notice-error">Saving turns off playlist sync and library scans until access is restored.</p>
      {/if}
      <div class="access-actions">
        <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save and restart"}</Button>
        {#if editing}<Button variant="secondary" type="button" disabled={busy} onclick={cancelEdit}>Cancel</Button>{/if}
      </div>
    </form>
  {/if}
  {#if saved}<p role="status">Access settings are saved. Waiting for Allstarr to restart…</p>{/if}
  {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
  {#if !busy && (error || saved)}<Button variant="secondary" onclick={() => void (saved ? save() : load())}>{saved ? "Check restart again" : "Reload access settings"}</Button>{/if}
</article>

<style>
  .access-card > header { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); padding: 0; }
  .access-form { display: grid; gap: var(--space-4); max-width: 30rem; }
  .access-toggle { display: flex; align-items: center; gap: var(--space-3); color: var(--color-ink); }
  .access-actions { display: flex; flex-wrap: wrap; gap: var(--space-3); }
  .access-restored {
    border: 1px solid color-mix(in srgb, var(--color-success) 60%, var(--color-edge));
    border-radius: var(--radius-sm);
    background: color-mix(in srgb, var(--color-success) 10%, transparent);
    color: var(--color-ink);
    padding: var(--space-3) var(--space-4);
  }
  :global(.access-checkbox) { border-color: transparent; background: transparent; }
  :global(.access-checkbox [data-slot="checkbox-indicator"]) {
    width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px;
  }
  :global(.access-checkbox[data-checked] [data-slot="checkbox-indicator"]) {
    border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary);
  }
</style>
