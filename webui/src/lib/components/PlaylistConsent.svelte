<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { KeyRound, X } from "@lucide/svelte";
  import { auth, type PlaylistConsentStatus } from "$lib/api";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";

  let { backend = "", focus = false }: { backend?: string; focus?: boolean } = $props();
  const jellyfin = $derived(backend.toLowerCase() === "jellyfin");
  let status = $state<PlaylistConsentStatus | null>(null);
  let password = $state("");
  let loading = $state(true);
  let loadFailed = $state(false);
  let pending = $state(false);
  let error = $state("");
  let panel = $state<HTMLElement>();
  let dialogOpen = $state(false), removeOpen = $state(false);
  let passwordInput = $state<HTMLInputElement>();
  let focusConsumed = false;

  $effect(() => {
    if (!focus) { focusConsumed = false; return; }
    if (loading || !status?.supported || focusConsumed) return;
    focusConsumed = true;
    panel?.scrollIntoView({ block: "start", behavior: "instant" });
    if (!status.granted || status.needsSignIn) dialogOpen = true;
  });

  function dateLabel(value: string | null) {
    return formatLocalDateTime(value, "");
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
      dialogOpen = false;
      toast.success(jellyfin
        ? (status.queuedSyncs ?? 0) > 0
          ? `Playlist editing allowed. Fixing the order of ${countLabel(status.queuedSyncs ?? 0, "playlist")} now.`
          : "Playlist editing allowed."
        : "Playlist management allowed for your listener account.");
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
      removeOpen = false;
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
  <RowsSkeleton count={1} />
{:else if status?.supported}
  <section bind:this={panel} id="playlist-consent" class="consent-row" aria-label={jellyfin ? "Playlist editing in Jellyfin" : "Playlist management"} aria-busy={pending}>
    <span class="consent-icon" aria-hidden="true"><KeyRound size={20} /></span>
    <div class="consent-copy"><h2>{jellyfin ? "Playlist editing in Jellyfin" : "Playlist management"}</h2>
      <p>{status.needsSignIn ? "Jellyfin no longer accepts the saved sign-in. Sign in again to keep playlists in order." : status.granted ? `${dateLabel(status.updatedAt) ? `Allowed since ${dateLabel(status.updatedAt)} · ` : ""}Allstarr keeps a sign-in for you, never your password.` : jellyfin ? "Keep your synced playlists in the same order as their source." : "Library reads and sign-in work without this access."}</p>
    </div>
    <StatePill tone={status.granted && !status.needsSignIn ? "ok" : "neutral"} text={status.granted && !status.needsSignIn ? "Allowed" : status.needsSignIn ? "Sign in again" : "Not yet"} />
    {#if status.granted || status.needsSignIn}<Button variant="text" class="consent-remove" disabled={pending} onclick={() => removeOpen = true}>{jellyfin ? "Remove" : "Revoke access"}</Button>{/if}
    {#if !status.granted || status.needsSignIn}<Button variant="outline" onclick={() => dialogOpen = true}>{jellyfin ? "Allow editing" : "Allow playlist management"}</Button>{/if}
    {#if error && !dialogOpen}<p class="notice-error" role="alert">{error}</p>{/if}
  </section>
{:else if loadFailed}<ErrorState title="Playlist management unavailable" detail={error} onretry={load} />{/if}

<Dialog.Root bind:open={dialogOpen} onOpenChange={open => { if (!open) password = ""; }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog playlist-consent-dialog" onOpenAutoFocus={event => { event.preventDefault(); passwordInput?.focus(); }}>
    <header><div><Dialog.Title>{jellyfin ? "Allow playlist editing?" : "Allow playlist management"}</Dialog.Title><Dialog.Description>{jellyfin ? "Jellyfin needs a sign-in from you before Allstarr can reorder or remove songs in your playlists." : "Allow Allstarr to create and update playlists for your signed-in Subsonic account."}</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close"><X size={18} /></Dialog.Close></header>
    <form onsubmit={event => { event.preventDefault(); void grant(); }}>
      <p class="consent-explanation"><KeyRound size={18} aria-hidden="true" /><span><strong>Allstarr keeps a sign-in token, never your password.</strong> You enter your password once to get that token. Your password is discarded after sign-in.</span></p>
      <label class="field"><span>{jellyfin ? "Jellyfin password" : "Subsonic password"}</span><input bind:this={passwordInput} bind:value={password} type="password" autocomplete="current-password" required maxlength="2000" disabled={pending} /></label>
      <ul class="consent-limits"><li>Only for playlists Allstarr syncs for you.</li><li>Remove it any time in Sources › Accounts.</li></ul>
      {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "text" })}>Not now</Dialog.Close><Button type="submit" disabled={pending || !password}>{pending ? "Allowing…" : jellyfin ? "Allow editing" : "Allow playlist management"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<ConfirmDialog bind:open={removeOpen} title="Remove playlist editing access?" description="Allstarr removes your saved sign-in. Your playlists and music stay. Synced playlists may need editing access again to keep their order." confirmLabel="Remove sign-in" {error} disabled={pending} closeOnConfirm={false} onConfirm={revoke} />

<style>
  .consent-row { display: flex; min-width: 0; flex-wrap: wrap; align-items: center; gap: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); padding: var(--space-3) var(--space-4); background: var(--color-panel); }
  .consent-icon { display: grid; width: 36px; height: 36px; place-items: center; border-radius: var(--radius-sm); background: var(--color-panel-raised); color: var(--color-ink-muted); }
  .consent-copy { flex: 1; min-width: 10rem; }.consent-copy h2 { margin: 0; font-size: var(--text-sm); font-weight: 600; }.consent-copy p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
  :global(.consent-remove) { color: var(--danger); }
  .consent-explanation { display: flex; gap: var(--space-3); align-items: flex-start; margin: 0; border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-signal-muted); color: var(--color-signal-text); font-size: var(--text-sm); }
  .consent-explanation :global(svg) { flex-shrink: 0; }.consent-explanation strong { display: block; }
  .consent-limits { margin: 0; padding-left: var(--space-5); color: var(--color-ink-muted); font-size: var(--text-sm); }
  :global(.playlist-consent-dialog) { max-width: min(32rem, calc(100vw - 2rem)); }
  @media (max-width: 599px) { .consent-copy { flex-basis: calc(100% - 52px); } .consent-row { padding: var(--space-3); } }
</style>
