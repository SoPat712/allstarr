<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { KeyRound, Trash2 } from "@lucide/svelte";
  import { auth, type PlaylistConsentStatus } from "$lib/api";
  import { Button } from "$lib/components/ui/button";

  let { backend = "", focus = false }: { backend?: string; focus?: boolean } = $props();
  const jellyfin = $derived(backend.toLowerCase() === "jellyfin");
  let status = $state<PlaylistConsentStatus | null>(null);
  let password = $state("");
  let loading = $state(true);
  let loadFailed = $state(false);
  let pending = $state(false);
  let error = $state("");
  let panel = $state<HTMLElement>();

  $effect(() => {
    if (!focus || !panel || loading) return;
    panel.scrollIntoView({ block: "start", behavior: "instant" });
    panel.querySelector<HTMLInputElement>("input")?.focus({ preventScroll: true });
  });

  function dateLabel(value: string | null) {
    if (!value) return "";
    const parsed = new Date(value);
    return Number.isNaN(parsed.valueOf()) ? "" : parsed.toLocaleString();
  }

  async function load() {
    loading = true;
    loadFailed = false;
    error = "";
    try {
      status = await auth.playlistConsent();
    } catch (cause) {
      loadFailed = true;
      error = cause instanceof Error ? cause.message : "Playlist access status could not be loaded.";
    } finally {
      loading = false;
    }
  }

  async function grant() {
    if (!password || pending) return;
    pending = true;
    error = "";
    try {
      status = await auth.grantPlaylistConsent(password);
      password = "";
      toast.success(jellyfin ? "Playlist editing allowed." : "Playlist management allowed for your listener account.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist access could not be allowed.";
    } finally {
      password = "";
      pending = false;
    }
  }

  async function revoke() {
    if (pending) return;
    pending = true;
    error = "";
    try {
      status = await auth.revokePlaylistConsent();
      toast.success(jellyfin ? "Saved Jellyfin sign-in removed." : "Playlist management revoked.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist access could not be revoked.";
    } finally {
      password = "";
      pending = false;
    }
  }

  onMount(() => void load());
</script>

{#if loading}
  <section class="panel consent-panel" aria-label="Loading playlist management" aria-busy="true">
    <p class="consent-muted">Checking playlist access…</p>
  </section>
{:else if status?.supported}
  <section bind:this={panel} id="playlist-consent" class="panel consent-panel" aria-labelledby="playlist-consent-title" aria-busy={pending}>
    <header>
      <span class="consent-icon" aria-hidden="true"><KeyRound size={20} /></span>
      <div>
        <h2 id="playlist-consent-title">{jellyfin ? "Let Allstarr edit your playlists" : "Playlist management"}</h2>
        <p>{jellyfin ? "Jellyfin only lets a playlist's owner reorder or remove songs. Sign in once and Allstarr keeps a Jellyfin sign-in for your account (not your password) so synced playlists keep the same order as their source. You can remove it at any time." : "Allow Allstarr to create and update playlists for your signed-in Subsonic account. Library reads and sign-in work without this access."}</p>
      </div>
    </header>

    {#if jellyfin && status.needsSignIn}
      <div class="consent-expired">
        <p class="consent-warning" role="status">Jellyfin no longer accepts the saved sign-in. Sign in again to keep playlists in order.</p>
        <Button variant="outline" class="consent-remove" disabled={pending} onclick={() => void revoke()}><Trash2 size={16} aria-hidden="true" />{pending ? "Removing…" : "Remove"}</Button>
      </div>
    {/if}

    {#if status.granted && !(jellyfin && status.needsSignIn)}
      <div class="consent-status">
        {#if jellyfin}
          <strong>{dateLabel(status.updatedAt) ? `Allowed since ${dateLabel(status.updatedAt)}` : "Allowed"}</strong>
          <Button variant="outline" class="consent-remove" disabled={pending} onclick={() => void revoke()}><Trash2 size={16} aria-hidden="true" />{pending ? "Removing…" : "Remove"}</Button>
        {:else}
          <div><strong>Allowed</strong>{#if dateLabel(status.updatedAt)}<small>Updated {dateLabel(status.updatedAt)}</small>{/if}</div>
          <Button variant="destructive" disabled={pending} onclick={() => void revoke()}>{pending ? "Revoking…" : "Revoke access"}</Button>
        {/if}
      </div>
    {:else}
      <form class="consent-form" onsubmit={(event) => { event.preventDefault(); void grant(); }}>
        <label class="field">
          <span>{jellyfin ? "Jellyfin password" : "Subsonic password"}</span>
          <input bind:value={password} type="password" autocomplete="current-password" required maxlength="2000" disabled={pending} />
          {#if !jellyfin}<small>Reauthenticate as the current listener. Allstarr cannot grant access for another user.</small>{/if}
        </label>
        <Button type="submit" disabled={pending || !password}>{pending ? "Allowing…" : jellyfin ? "Allow playlist editing" : "Allow playlist management"}</Button>
      </form>
    {/if}

    {#if error}<p class="notice-error" role="alert">{error} <button class="text-button" type="button" onclick={() => void load()}>Retry</button></p>{/if}

  </section>
{:else if loadFailed}
  <section class="panel consent-panel consent-error" aria-labelledby="playlist-consent-error-title">
    <div>
      <h2 id="playlist-consent-error-title">Playlist management unavailable</h2>
      <p class="consent-muted" role="alert">{error}</p>
    </div>
    <Button variant="secondary" onclick={() => void load()}>Retry</Button>
  </section>
{/if}

<style>
  .consent-panel{display:grid;gap:1rem;border-radius:var(--radius-lg);padding:var(--space-5) var(--space-6);scroll-margin-top:var(--space-5)}
  .consent-warning{color:var(--color-warning);font-size:var(--text-sm)}
  .consent-expired{display:flex;align-items:center;justify-content:space-between;gap:1rem;flex-wrap:wrap}
  :global(.consent-remove:hover){color:var(--md-sys-color-error);border-color:var(--md-sys-color-error);background:color-mix(in srgb,var(--md-sys-color-error) 8%,transparent)}
  .consent-panel h2{font-size:1rem;line-height:1.5rem;font-weight:600}
  .consent-muted{color:var(--color-ink-muted);font-size:var(--text-sm)}
  .consent-error{grid-template-columns:minmax(0,1fr) auto;align-items:center}
  .consent-panel header{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:start;gap:.8rem}
  .consent-panel h2,.consent-panel p{margin:0}.consent-panel header p:last-child{margin-top:.3rem;color:var(--color-ink-muted)}
  .consent-icon{display:grid;place-items:center;width:2.75rem;height:2.75rem;border-radius:50%;background:var(--color-signal-muted);color:var(--color-signal-text)}
  .consent-form,.consent-status{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:1rem}
  .consent-status{align-items:center;border-top:1px solid var(--color-edge);padding-top:1rem}.consent-status div,.consent-status small{display:block}.consent-status small{margin-top:.2rem;color:var(--color-ink-muted)}
  .field small{color:var(--color-ink-muted)}.text-button{min-height:2.75rem;color:var(--color-signal-text);text-decoration:underline}
  @media(max-width:620px){.consent-error{grid-template-columns:1fr}.consent-panel{padding:var(--space-4)}.consent-form,.consent-status{grid-template-columns:1fr}.consent-form>:global([data-slot="button"]),.consent-status>:global([data-slot="button"]){width:100%}}
</style>
