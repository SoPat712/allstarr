<script lang="ts">
  import { onMount } from "svelte";
  import { setupAccess, type OidcAccess, type BackendAccess } from "$lib/api";
  import { waitForRestart } from "$lib/restart";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  let { kind }: { kind: "oidc" | "backend" } = $props();
  let snapshot = $state<OidcAccess | BackendAccess | null>(null), csrf = $state("");
  let enabled = $state(false), authority = $state(""), clientId = $state(""), publicUrl = $state(""), displayName = $state("Single sign-on");
  let secret = $state(""), removeSecret = $state(false), username = $state(""), password = $state("");
  let busy = $state(false), error = $state(""), saved = $state(false);
  const abort = new AbortController();
  const oidc = $derived(kind === "oidc" ? snapshot as OidcAccess | null : null);
  const backend = $derived(kind === "backend" ? snapshot as BackendAccess | null : null);
  async function load() {
    error = "";
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
        await setupAccess.save(kind, value, csrf);
        saved = true;
      }
      secret = ""; password = "";
      await waitForRestart(abort.signal);
      window.location.reload();
    } catch (cause) { error = cause instanceof Error ? cause.message : "Access could not be saved. Reload the settings and retry."; }
    finally { secret = ""; password = ""; busy = false; }
  }
  onMount(() => { void load(); return () => abort.abort(); });
</script>
<article class="settings-card p-5 space-y-4" aria-label={kind === "oidc" ? "Single sign-on" : "Background library access"}>
  <header><h3>{kind === "oidc" ? "Single sign-on" : "Background library access"}</h3></header>
  <p>{kind === "oidc" ? "Connect an OpenID Connect identity provider. Media-server login remains available. Secrets are encrypted and never displayed." : "Allow unattended Jellyfin library scans with your verified administrator access token. Your password is used only for verification and is discarded. Each listener's library permissions remain authoritative."}</p>
  {#if snapshot?.needsReview}<p class="notice-error">The saved connection needs attention. Enter fresh credentials or turn it off.</p>{/if}
  {#if backend && (!backend.supported || !backend.connectionSaved)}<p>{!backend.supported ? "Subsonic access is granted separately by each listener." : "Preview and import the mounted settings to save the current media-server connection first."}</p>
  {:else if snapshot && !saved}
    <form class="space-y-4" onsubmit={(event) => { event.preventDefault(); void save(); }}>
      <label class="flex items-center gap-3"><Checkbox bind:checked={enabled} disabled={busy} /><span>{kind === "oidc" ? "Enable single sign-on" : "Allow background scans"}</span></label>
      {#if kind === "oidc"}
        <label class="block">Issuer address<input class="w-full mt-1" type="url" bind:value={authority} disabled={busy} required={enabled} /></label>
        <label class="block">Client ID<input class="w-full mt-1" bind:value={clientId} disabled={busy} required={enabled} /></label>
        <label class="block">Public Allstarr address<input class="w-full mt-1" type="url" bind:value={publicUrl} disabled={busy} required={enabled} /></label>
        <label class="block">Sign-in button name<input class="w-full mt-1" bind:value={displayName} disabled={busy} /></label>
        <label class="block">Client secret<input class="w-full mt-1" type="password" aria-label="Client secret" autocomplete="new-password" bind:value={secret} disabled={busy || removeSecret} /><small>{oidc?.hasClientSecret ? "A secret is saved. Leave blank to keep it for the same issuer and client." : "No secret is saved."}</small></label>
        <label class="flex items-center gap-3"><Checkbox bind:checked={removeSecret} disabled={busy} /><span>Remove saved client secret</span></label>
      {:else if enabled}
        <label class="block">Administrator username<input class="w-full mt-1" autocomplete="username" bind:value={username} required disabled={busy} /></label>
        <label class="block">Administrator password<input class="w-full mt-1" type="password" autocomplete="current-password" bind:value={password} required disabled={busy} /></label>
      {/if}
      <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save and restart"}</Button>
    </form>
  {/if}
  {#if saved}<p role="status">Access settings are saved. Waiting for Allstarr to restart…</p>{/if}
  {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
  {#if !busy && (error || saved)}<Button variant="secondary" onclick={() => void (saved ? save() : load())}>{saved ? "Check restart again" : "Reload access settings"}</Button>{/if}
</article>
