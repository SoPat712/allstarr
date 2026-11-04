<script lang="ts">
  import ServiceAccessRestore from "$lib/components/ServiceAccessRestore.svelte";
  import { rememberRestartUndo } from "$lib/settings-restart";
  import { onMount } from "svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import { Switch } from "$lib/components/ui/switch";
  import { setupAccess, type OidcAccess, type BackendAccess } from "$lib/api";
  import { consumeAccessRestored, markAccessRestored, savedBeforeRestart, waitForRestart } from "$lib/restart";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  let { kind }: { kind: "oidc" | "backend" } = $props();
  let snapshot = $state<OidcAccess | BackendAccess | null>(null), csrf = $state("");
  let enabled = $state(false), authority = $state(""), clientId = $state(""), publicUrl = $state(""), displayName = $state("Single sign-on");
  let secret = $state(""), removeSecret = $state(false), username = $state(""), password = $state("");
  let busy = $state(false), error = $state(""), saved = $state(false), editing = $state(false), restored = $state(false);
  let confirmOpen = $state(false), form = $state<HTMLFormElement>();
  let confirming = $state(false);
  const abort = new AbortController();
  function requestSave() {
    if (busy || !snapshot || !form?.checkValidity()) return;
    confirming = true; confirmOpen = true;
  }
  $effect(() => {
    if (confirming && !confirmOpen && !busy && !saved) {
      confirming = false;
      if (oidc) { enabled = oidc.enabled; authority = oidc.authority; clientId = oidc.clientId; publicUrl = oidc.publicUrl; displayName = oidc.displayName || "Single sign-on"; }
      else enabled = backend?.configured ?? false;
      secret = ""; password = ""; removeSecret = false;
    }
  });
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
        if (oidc && authority === oidc.authority && clientId === oidc.clientId && !secret && !removeSecret) rememberRestartUndo({ kind: "oidc", label: "Single sign-on", settings: { enabled: oidc.enabled, authority: oidc.authority, clientId: oidc.clientId, publicUrl: oidc.publicUrl, displayName: oidc.displayName } });
        saved = true; confirmOpen = false; confirming = false;
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
<article class="settings-card access-card" aria-label={kind === "oidc" ? "Single sign-on" : "Media-server access"}>
  <header><h3>{kind === "oidc" ? "Single sign-on" : "Media server"}</h3></header>
  {#if !snapshot && !error}<RowsSkeleton count={1} />
  {:else if backend && (!backend.supported || !backend.connectionSaved)}
    <p class="settings-impact">{!backend.supported ? "Subsonic access is granted separately by each listener." : "Review previous settings in the setup guide to save your media-server connection."}</p>
  {:else if backend && connected && !editing && !saved}
    <div class="settings-row"><div><strong>Media-server access</strong><small>Playlist sync and library scans. Each listener's own library permissions still apply.</small>
      {#if restored}<small role="status">Media-server access is restored.</small>{/if}
      {#if backend.credentialKind === "api-key"}<small>Administrator access enables playlist order and removal sync.</small>{/if}
    </div><div class="connection-actions"><Badge tone="success">Connected</Badge><Button variant="outline" onclick={() => { editing = true; restored = false; }}>Change access</Button></div></div>
  {:else if snapshot && !saved}
    <form bind:this={form} class="access-form" onchange={() => requestSave()} onsubmit={event => { event.preventDefault(); requestSave(); }}>
      <div class="settings-row"><div><strong>{kind === "oidc" ? "Sign in with your identity provider" : "Allow playlist sync and library scans"}</strong><small>{kind === "oidc" ? "OpenID Connect. Connection fields appear when switched on. Media-server sign-in stays available." : "Verify an administrator sign-in. The password is discarded after verification."}</small><small>Restarts Allstarr</small></div><span class="settings-toggle"><Switch aria-label={kind === "oidc" ? "Enable single sign-on" : "Allow playlist sync and library scans"} bind:checked={enabled} disabled={busy} /></span></div>
      {#if kind === "oidc" && enabled}
        {#if snapshot.needsReview}<p class="settings-impact notice-error">The saved connection needs attention. Enter fresh credentials or turn it off.</p>{/if}
        <label class="settings-row"><div><strong>Issuer address</strong></div><input type="url" aria-label="Issuer address" bind:value={authority} disabled={busy} required /></label>
        <label class="settings-row"><div><strong>Client ID</strong></div><input aria-label="Client ID" bind:value={clientId} disabled={busy} required /></label>
        <label class="settings-row"><div><strong>Public Allstarr address</strong></div><input type="url" aria-label="Public Allstarr address" bind:value={publicUrl} disabled={busy} required /></label>
        <label class="settings-row"><div><strong>Sign-in button name</strong></div><input aria-label="Sign-in button name" bind:value={displayName} disabled={busy} /></label>
        <label class="settings-row"><div><strong>Client secret</strong><small>{oidc?.hasClientSecret ? "A secret is saved. Leave blank to keep it for the same issuer and client." : "No secret is saved."}</small></div><input type="password" aria-label="Client secret" autocomplete="new-password" bind:value={secret} disabled={busy || removeSecret} /></label>
        {#if oidc?.hasClientSecret}<label class="settings-row"><div><strong>Remove saved client secret</strong></div><span class="settings-toggle"><Switch aria-label="Remove saved client secret" bind:checked={removeSecret} disabled={busy} /></span></label>{/if}
      {:else if kind === "backend" && enabled}
        <label class="settings-row"><div><strong>Jellyfin administrator username</strong></div><input aria-label="Jellyfin administrator username" autocomplete="username" bind:value={username} required disabled={busy} /></label>
        <label class="settings-row"><div><strong>Jellyfin administrator password</strong></div><input aria-label="Jellyfin administrator password" type="password" autocomplete="current-password" bind:value={password} required disabled={busy} /></label>
      {:else if kind === "backend" && connected}<p class="settings-impact notice-error">Turning this off stops playlist sync and library scans until access is restored.</p>{/if}
      {#if kind === "backend"}<footer><Button variant="outline" type="submit" disabled={busy}>Verify access</Button>{#if editing}<Button variant="text" type="button" disabled={busy} onclick={cancelEdit}>Cancel</Button>{/if}</footer>{/if}
    </form>
  {/if}
  {#if saved}<p class="settings-impact" role="status">Access settings are saved. Waiting for Allstarr to restart…</p>{/if}
  {#if error}<ErrorState title="Access could not be saved" detail={error} onretry={() => saved ? save() : load()} />{/if}
</article>
<ConfirmDialog bind:open={confirmOpen} title="Restart Allstarr to apply?" description="Changing sign-in or media-server access needs a restart. Music that is playing pauses briefly. This page reconnects when Allstarr is ready." confirmLabel={busy ? "Applying…" : "Restart and apply"} confirmVariant="default" disabled={busy} closeOnConfirm={false} {error} onConfirm={save} />
<style>
  .access-form { min-width: 0; }
  .connection-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: end; gap: var(--space-2); }
  @media (max-width: 650px) { .connection-actions { justify-content: start; } }
</style>
