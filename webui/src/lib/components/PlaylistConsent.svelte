<script lang="ts">
  import { onMount } from "svelte";
  import { KeyRound } from "@lucide/svelte";
  import { auth, type PlaylistConsentStatus } from "$lib/api";
  import { Button } from "$lib/components/ui/button";

  let status = $state<PlaylistConsentStatus | null>(null);
  let password = $state("");
  let loading = $state(true);
  let loadFailed = $state(false);
  let pending = $state(false);
  let error = $state("");
  let feedback = $state("");

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
    feedback = "";
    try {
      status = await auth.grantPlaylistConsent(password);
      password = "";
      feedback = "Playlist management allowed for your listener account.";
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
    feedback = "";
    try {
      status = await auth.revokePlaylistConsent();
      feedback = "Playlist management revoked.";
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
    <p class="eyebrow">Native playlists</p><p>Checking playlist access…</p>
  </section>
{:else if status?.supported}
  <section class="panel consent-panel" aria-labelledby="playlist-consent-title" aria-busy={pending}>
    <header>
      <span class="consent-icon" aria-hidden="true"><KeyRound size={20} /></span>
      <div>
        <p class="eyebrow">Native playlists</p>
        <h2 id="playlist-consent-title">Playlist management</h2>
        <p>Allow Allstarr to create and update playlists for your signed-in Subsonic account. Library reads and sign-in work without this access.</p>
      </div>
    </header>

    {#if status.granted}
      <div class="consent-status">
        <div><strong>Allowed</strong>{#if dateLabel(status.updatedAt)}<small>Updated {dateLabel(status.updatedAt)}</small>{/if}</div>
        <Button variant="destructive" disabled={pending} onclick={() => void revoke()}>{pending ? "Revoking…" : "Revoke access"}</Button>
      </div>
    {:else}
      <form class="consent-form" onsubmit={(event) => { event.preventDefault(); void grant(); }}>
        <label class="field">
          <span>Subsonic password</span>
          <input bind:value={password} type="password" autocomplete="current-password" required maxlength="2000" disabled={pending} />
          <small>Reauthenticate as the current listener. Allstarr cannot grant access for another user.</small>
        </label>
        <Button type="submit" disabled={pending || !password}>{pending ? "Allowing…" : "Allow playlist management"}</Button>
      </form>
    {/if}

    {#if error}<p class="notice-error" role="alert">{error} <button class="text-button" type="button" onclick={() => void load()}>Retry</button></p>{/if}
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
  </section>
{:else if loadFailed}
  <section class="panel consent-panel" aria-labelledby="playlist-consent-error-title">
    <div>
      <p class="eyebrow">Native playlists</p>
      <h2 id="playlist-consent-error-title">Playlist management unavailable</h2>
    </div>
    <p class="notice-error" role="alert">{error}</p>
    <Button variant="secondary" onclick={() => void load()}>Retry</Button>
  </section>
{/if}

<style>
  .consent-panel{display:grid;gap:1rem;padding:var(--surface-gutter)}
  .consent-panel header{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:start;gap:.8rem}
  .consent-panel h2,.consent-panel p{margin:0}.consent-panel header p:last-child{margin-top:.3rem;color:var(--color-ink-muted)}
  .consent-icon{display:grid;place-items:center;width:2.75rem;height:2.75rem;border-radius:50%;background:var(--color-signal-muted);color:var(--color-signal-text)}
  .consent-form,.consent-status{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:1rem}
  .consent-status{align-items:center;border-top:1px solid var(--color-edge);padding-top:1rem}.consent-status div,.consent-status small{display:block}.consent-status small{margin-top:.2rem;color:var(--color-ink-muted)}
  .field small{color:var(--color-ink-muted)}.text-button{min-height:2.75rem;color:var(--color-signal-text);text-decoration:underline}
  @media(max-width:620px){.consent-form,.consent-status{grid-template-columns:1fr}.consent-form>:global([data-slot="button"]),.consent-status>:global([data-slot="button"]){width:100%}}
</style>
