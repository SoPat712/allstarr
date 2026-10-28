<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, listeningPreferences, playlistLinks, sources, type ListenerPlaylistCopy, type PersonalListeningPreferences, type PlaylistLink } from "$lib/api";
  import { originalCopyName } from "$lib/listeners";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Skeleton } from "$lib/components/ui/skeleton";

  type Listener = { id: string; displayName: string; preferences: PersonalListeningPreferences | null;
    copies: ListenerPlaylistCopy[] | null; error: string };
  let listeners = $state<Listener[]>([]), links = $state<PlaylistLink[]>([]);
  let loading = $state(true), error = $state(""), action = $state("");
  let fetching = false;

  async function load() {
    if (fetching || action) return;
    fetching = true;
    try {
      const [users, playlists] = await Promise.allSettled([sources.accounts(), playlistLinks.list()]);
      if (users.status === "rejected") throw users.reason;
      if (playlists.status === "fulfilled") links = playlists.value.playlistLinks ?? [];
      listeners = await Promise.all((users.value.audienceUsers ?? []).map(async user => {
        const previous = listeners.find(item => item.id === user.id);
        const [preferences, copies] = await Promise.allSettled([
          listeningPreferences.lock(user.id), listeningPreferences.copies(user.id),
        ]);
        return { ...user,
          preferences: preferences.status === "fulfilled" ? preferences.value : previous?.preferences ?? null,
          copies: copies.status === "fulfilled" ? copies.value : previous?.copies ?? null,
          error: preferences.status === "rejected" || copies.status === "rejected" ? "Some listener settings could not be loaded. Refresh to try again." : "",
        };
      }));
      error = playlists.status === "rejected" ? "Original playlist names could not be loaded. Refresh to try again." : "";
    } catch {
      error = "Listeners could not be loaded. Refresh to try again.";
    } finally { fetching = false; loading = false; }
  }

  async function toggle(listener: Listener, locked: boolean) {
    if (!listener.preferences || action || fetching) return;
    action = listener.id;
    try {
      listener.preferences = await listeningPreferences.saveLock(listener.id, locked, listener.preferences.revision);
      toast.success("Listener settings saved.");
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 409) {
        try {
          listener.preferences = await listeningPreferences.lock(listener.id);
          toast.info("Changed elsewhere — reloaded.");
        } catch { listener.preferences = null; toast.error("Listener settings could not be reloaded. Refresh to try again."); }
      } else toast.error("Listener settings could not be saved. Try again.");
    } finally { action = ""; }
  }

  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { unsubscribe(); scheduler.cancel(); };
  });
</script>

{#if loading}
  <Skeleton class="panel skeleton-panel" aria-label="Loading listeners" />
{:else}
  <div class="flex justify-end"><RefreshButton variant="secondary" onrefresh={load} /></div>
  {#if error && !listeners.length}
    <RouteError eyebrow="Listeners unavailable" title="Listeners could not be loaded." message={error} onRetry={load} />
  {:else}
    {#if error}<p class="notice-error" role="status">{error}</p>{/if}
    {#each listeners as listener (listener.id)}
      <section class="settings-card" aria-label={listener.displayName}>
        <header><h3>{listener.displayName}</h3></header>
        <label class="settings-row">
          <div><strong>Lock clean only</strong><small>This listener can't turn it off.</small></div>
          <span class="settings-toggle"><Checkbox aria-label="Lock clean only"
            bind:checked={() => listener.preferences?.cleanOnlyLocked ?? false, checked => void toggle(listener, checked)}
            disabled={Boolean(action) || !listener.preferences} /></span>
        </label>
        {#if listener.error}<p class="settings-impact" role="status">{listener.error}</p>{/if}
        <details class="copies">
          <summary>Playlist copies</summary>
          {#if listener.copies === null}
            <p>Personal copies could not be loaded.</p>
          {:else if !listener.copies.length}
            <p>No personal copies</p>
          {:else}
            <ul>{#each listener.copies as copy (copy.id)}<li>{copy.name} → {originalCopyName(copy, links)}</li>{/each}</ul>
          {/if}
        </details>
      </section>
    {:else}
      <section class="settings-card"><p class="settings-impact">No listeners</p></section>
    {/each}
  {/if}
{/if}

<style>
  .settings-toggle :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .settings-toggle :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .settings-toggle :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  .copies { border-top: 1px solid var(--color-edge); padding: 0 var(--space-6) var(--space-2); }
  .copies summary { min-height: var(--control-md); line-height: var(--control-md); cursor: pointer; font-size: var(--text-sm); }
  .copies p, .copies ul { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-sm); overflow-wrap: anywhere; }
  .copies ul { padding-left: var(--space-4); }
  @media (max-width: 650px) { .copies { padding-inline: var(--space-4); } }
</style>
