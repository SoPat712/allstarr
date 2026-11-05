<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { ApiError, listeningPreferences, playlistLinks, sources, type ListenerPlaylistCopy, type PersonalListeningPreferences, type PlaylistLink, type ProviderAccount } from "$lib/api";
  import { originalCopyName } from "$lib/listeners";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Users } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";

  type Listener = { id: string; displayName: string; preferences: PersonalListeningPreferences | null; copies: ListenerPlaylistCopy[] | null; error: string };
  let { initialPanel = "" }: { initialPanel?: string } = $props();
  let listeners = $state<Listener[]>([]), links = $state<PlaylistLink[]>([]), accounts = $state<ProviderAccount[]>([]);
  let loading = $state(true), error = $state(""), action = $state(""), selected = $state(""), panelOpen = $state(false), filter = $state("all");
  let fetching = false, initialized = false;
  const current = $derived(listeners.find(item => item.id === selected));
  const lockedCount = $derived(listeners.filter(item => item.preferences?.cleanOnlyLocked).length);
  const filtered = $derived(listeners.filter(item => filter === "all" || item.preferences?.cleanOnlyLocked));
  const selectedIndex = $derived(filtered.findIndex(item => item.id === selected));
  const ownAccounts = (id: string) => accounts.filter(account => account.ownerUserId === id);
  async function load() {
    if (fetching || action) return;
    fetching = true;
    try {
      const [users, playlists] = await Promise.allSettled([sources.accounts(), playlistLinks.list()]);
      if (users.status === "rejected") throw users.reason;
      accounts = users.value.accounts ?? [];
      if (playlists.status === "fulfilled") links = playlists.value.playlistLinks ?? [];
      listeners = await Promise.all((users.value.audienceUsers ?? []).map(async user => {
        const previous = listeners.find(item => item.id === user.id);
        const [preferences, copies] = await Promise.allSettled([listeningPreferences.lock(user.id), listeningPreferences.copies(user.id)]);
        return { ...user, preferences: preferences.status === "fulfilled" ? preferences.value : previous?.preferences ?? null,
          copies: copies.status === "fulfilled" ? copies.value : previous?.copies ?? null,
          error: preferences.status === "rejected" || copies.status === "rejected" ? "Some listener settings could not be loaded. Try again." : "" };
      }));
      if (!initialized && initialPanel && listeners.some(item => item.id === initialPanel)) { selected = initialPanel; panelOpen = true; }
      initialized = true;
      error = playlists.status === "rejected" ? "Original playlist names could not be loaded. Try again." : "";
    } catch { error = "Listeners could not be loaded. Try again."; }
    finally { fetching = false; loading = false; }
  }
  async function toggle(listener: Listener, locked: boolean, undo = true) {
    if (!listener.preferences || action || fetching) return;
    const previous = listener.preferences;
    listener.preferences = { ...previous, cleanOnlyLocked: locked };
    action = listener.id;
    try {
      listener.preferences = await listeningPreferences.saveLock(listener.id, locked, listener.preferences.revision);
      toast.success("Listener settings saved.", undo ? { label: "Undo", run: async () => { const current = listeners.find(item => item.id === listener.id); if (current) await toggle(current, previous.cleanOnlyLocked ?? false, false); } } : undefined);
    } catch (cause) {
      listener.preferences = previous;
      if (cause instanceof ApiError && cause.status === 409) {
        try { listener.preferences = await listeningPreferences.lock(listener.id); toast.info("Changed elsewhere — reloaded."); }
        catch { listener.preferences = null; toast.error("Listener settings could not be reloaded. Try again."); }
      } else toast.error("Listener settings could not be saved. Try again.");
    } finally { action = ""; }
  }
  function open(listener: Listener) { selected = listener.id; panelOpen = true; }
  const scheduler = createRefreshScheduler(load);
  onMount(() => { void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule); return () => { unsubscribe(); scheduler.cancel(); }; });
</script>

<div class="settings-stack">
  <PageStatus items={[{ label: countLabel(listeners.length, "listener") }, { label: `${lockedCount} locked to clean` }]} />
  {#if loading}<RowsSkeleton />
  {:else if error && !listeners.length}<ErrorState title="Listeners could not be loaded" detail={error} onretry={load} />
  {:else}
    {#if error}<ErrorState title="Some listener details are unavailable" detail={error} onretry={load} />{/if}
    <div class="settings-toolbar"><FilterSegments bind:value={filter} storageKey="allstarr.settings.listeners.filter" label="Filter listeners" options={[{ id: "all", label: "All", count: listeners.length }, { id: "locked", label: "Locked to clean", count: lockedCount }]} /><Button variant="text" onclick={load}>Refresh</Button></div>
    <section class="settings-card" aria-label="Listeners">
      {#if filtered.length}
        <table class="settings-table"><thead><tr><th>Listener</th><th class="desktop-column">Last active</th><th class="desktop-column">Plays this week</th><th class="desktop-column">Accounts</th><th class="desktop-column">Playlist editing</th><th>Clean only, locked</th><th></th></tr></thead><tbody>
          {#each filtered as listener (listener.id)}
            <tr aria-label={listener.displayName} class:selected={selected === listener.id && panelOpen}>
              <td><button class="row-button" type="button" aria-label={`Open ${listener.displayName}`} onclick={() => open(listener)}>{listener.displayName}</button></td>
              <td class="desktop-column settings-muted">Unavailable</td><td class="desktop-column settings-muted">—</td>
              <td class="desktop-column">{ownAccounts(listener.id).map(account => account.displayName).join(", ") || "—"}</td>
              <td class="desktop-column settings-muted">Unavailable</td>
              <td><Switch class="table-control" aria-label={`Lock clean only for ${listener.displayName}`} checked={listener.preferences?.cleanOnlyLocked ?? false} disabled={Boolean(action) || !listener.preferences} onchange={event => void toggle(listener, event.currentTarget.checked)} /></td>
              <td aria-hidden="true">›</td>
            </tr>
          {/each}
        </tbody></table>
      {:else}<EmptyState title={listeners.length ? "No listeners locked to clean" : "No listeners yet"} description="Listeners appear after using Allstarr with their media-server account.">{#snippet icon()}<Users size={24} />{/snippet}</EmptyState>{/if}
    </section>
  {/if}
</div>

{#if current}
  {#await import("./patterns/SidePanel.svelte") then { default: SidePanel }}
    <SidePanel bind:open={panelOpen} title={current.displayName} description="Listener" navigationLabel="listener" onPrevious={selectedIndex > 0 ? () => open(filtered[selectedIndex - 1]) : undefined} onNext={selectedIndex >= 0 && selectedIndex < filtered.length - 1 ? () => open(filtered[selectedIndex + 1]) : undefined}>
      <div class="settings-stack">
        <PageStatus items={[{ label: `${current.copies?.length ?? 0} playlist copies` }, { label: `${ownAccounts(current.id).length} own accounts` }]} />
        {#if current.error}<ErrorState title="Some listener details are unavailable" detail={current.error} onretry={load} />{/if}
        <section class="settings-card"><div class="settings-row"><div><strong>Clean only, locked</strong><small>This listener can't turn explicit songs back on.</small></div><Switch aria-label="Lock clean only" checked={current.preferences?.cleanOnlyLocked ?? false} disabled={Boolean(action) || !current.preferences} onchange={event => void toggle(current!, event.currentTarget.checked)} /></div></section>
        <p class="settings-muted">Each listener allows playlist editing in Sources › Accounts.</p>
        <section class="settings-card" aria-label="Playlist copies"><header><h3>Playlist copies</h3></header>
          {#if current.copies === null}<ErrorState title="Personal copies could not be loaded" onretry={load} />
          {:else if !current.copies.length}<EmptyState title="No personal copies" description="A listener's own versions of playlists appear here." />
          {:else}{#each current.copies as copy (copy.id)}<ItemRow title={copy.name} subtitle={`From ${originalCopyName(copy, links)}`} />{/each}{/if}
        </section>
        <section class="settings-card"><header><h3>Own accounts</h3></header>{#each ownAccounts(current.id) as account (account.id)}<ItemRow title={account.displayName} subtitle={account.providerId} />{:else}<EmptyState title="No own accounts" description="Listeners connect their own accounts in Sources." />{/each}</section>
      </div>
      {#snippet footer()}<Button variant="outline" href="#/activity">See activity</Button>{/snippet}
    </SidePanel>
  {/await}
{/if}
