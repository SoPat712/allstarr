<script lang="ts">
  import RefreshButton from "$lib/components/RefreshButton.svelte";
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
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import { rememberActivityScope } from "$lib/activity-scope";
  import { promotionProfiles, type HouseholdPromotionProfile } from "$lib/promotion-profile-api";

  type Listener = { id: string; displayName: string; isAdministrator?: boolean; isTrustedListener: boolean; lastActiveAt?: string | null; playsThisWeek?: number; playlistEditingAllowed?: boolean; recentPlays?: { title: string | null; artist: string | null; playedAt: string }[]; preferences: PersonalListeningPreferences | null; copies: ListenerPlaylistCopy[] | null; error: string };
  let { initialPanel = "" }: { initialPanel?: string } = $props();
  let listeners = $state<Listener[]>([]), links = $state<PlaylistLink[]>([]), accounts = $state<ProviderAccount[]>([]);
  let loading = $state(true), error = $state(""), action = $state(""), selected = $state(""), panelOpen = $state(false), filter = $state("all");
  let ownConnections = $state<boolean | null>(null);
  let household = $state<HouseholdPromotionProfile | null>(null), policyError = $state("");
  let fetching = $state(false);
  let fetchingPolicy = false, initialized = false;
  const current = $derived(listeners.find(item => item.id === selected));
  const lockedCount = $derived(listeners.filter(item => item.preferences?.cleanOnlyLocked).length);
  const filtered = $derived(listeners.filter(item => filter === "all" || item.preferences?.cleanOnlyLocked));
  const selectedIndex = $derived(filtered.findIndex(item => item.id === selected));
  const ownAccounts = (id: string) => accounts.filter(account => account.ownerUserId === id);
  const lastActive = (value?: string | null) => value ? new Date(value).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }) : "Never";
  async function load() {
    if (fetching || action) return;
    fetching = true;
    try {
      const [users, playlists] = await Promise.allSettled([sources.accounts(), playlistLinks.list()]);
      if (users.status === "rejected") throw users.reason;
      accounts = users.value.accounts ?? [];
      ownConnections = typeof users.value.listenersCanConnectOwnAccounts === "boolean" ? users.value.listenersCanConnectOwnAccounts : null;
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
  async function loadPolicy() {
    if (fetchingPolicy || action) return;
    fetchingPolicy = true; household = null; policyError = "";
    try {
      const profile = await promotionProfiles.household();
      if (profile?.addWithoutApproval !== "admins" && profile?.addWithoutApproval !== "admins-and-trusted-listeners") throw new Error("Household approval setting is unavailable.");
      household = profile;
    } catch { policyError = "Listener library permissions are unavailable until the household setting can be loaded. Try again."; }
    finally { fetchingPolicy = false; }
  }
  async function setTrust(listener: Listener, trusted: boolean) {
    if (action || fetching || fetchingPolicy || household?.addWithoutApproval !== "admins-and-trusted-listeners") return;
    const previous = listener.isTrustedListener;
    listener.isTrustedListener = trusted; action = listener.id;
    try {
      const saved = await sources.setListenerTrust(listener.id, trusted);
      listener.isTrustedListener = saved.isTrustedListener;
      toast.success("Listener library permission saved.");
    } catch {
      listener.isTrustedListener = previous;
      toast.error("Listener library permission could not be saved. Try again.");
    } finally { action = ""; }
  }
  async function refresh() { await Promise.all([load(), loadPolicy()]); }
  function open(listener: Listener) { selected = listener.id; panelOpen = true; }
  const scheduler = createRefreshScheduler(refresh);
  onMount(() => { void refresh(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule); return () => { unsubscribe(); scheduler.cancel(); }; });
</script>

<div class="settings-stack">
  <PageStatus items={[{ label: countLabel(listeners.length, "listener") }, { label: countLabel(listeners.filter(listener => listener.isAdministrator).length, "administrator") }, { label: `${lockedCount} locked to clean` }]} />
  {#if loading}<RowsSkeleton />
  {:else if error && !listeners.length}<ErrorState title="Listeners could not be loaded" detail={error} onretry={load} />
  {:else}
    {#if error}<ErrorState title="Some listener details are unavailable" detail={error} onretry={load} />{/if}
    <div class="settings-toolbar"><FilterSegments bind:value={filter} storageKey="allstarr.settings.listeners.filter" label="Filter listeners" options={[{ id: "all", label: "All", count: listeners.length }, { id: "locked", label: "Locked to clean", count: lockedCount }]} /><RefreshButton onrefresh={refresh} label="Refresh listeners" /></div>
    <section class="settings-card" aria-label="Listeners">
      {#if filtered.length}
        <table class="settings-table listener-table"><thead><tr><th>Listener</th><th class="desktop-column">Last active</th><th class="desktop-column">Plays this week</th><th class="desktop-column">Accounts</th><th class="desktop-column">Playlist editing</th><th>Clean only, locked</th><th></th></tr></thead><tbody>
          {#each filtered as listener (listener.id)}
            <tr aria-label={listener.displayName} class:selected={selected === listener.id && panelOpen}>
              <td><button class="row-button" type="button" aria-label={`Open ${listener.displayName}`} onclick={() => open(listener)}>{listener.displayName}</button>{#if listener.isAdministrator}<StatePill text="Admin" />{/if}</td>
              <td class="desktop-column settings-muted">{lastActive(listener.lastActiveAt)}</td><td class="desktop-column number">{listener.playsThisWeek?.toLocaleString() ?? "—"}</td>
              <td class="desktop-column">{ownAccounts(listener.id).map(account => account.displayName).join(", ") || "—"}</td>
              <td class="desktop-column">{#if listener.playlistEditingAllowed}<StatePill tone="ok" text="Allowed" />{:else}<span class="settings-muted">Not yet</span>{/if}</td>
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
    <SidePanel bind:open={panelOpen} title={current.displayName} description={current.lastActiveAt ? `Listener · last active ${lastActive(current.lastActiveAt)}` : "Listener · Never active"} navigationLabel="listener" onPrevious={selectedIndex > 0 ? () => open(filtered[selectedIndex - 1]) : undefined} onNext={selectedIndex >= 0 && selectedIndex < filtered.length - 1 ? () => open(filtered[selectedIndex + 1]) : undefined}>
      <div class="settings-stack settings-surface listener-details">
        <div class="listener-kpis" aria-label="Listener totals"><div><strong>{current.playsThisWeek?.toLocaleString() ?? "—"}</strong><small>Plays in 7 days</small></div><div><strong>{current.copies?.length ?? 0}</strong><small>Playlist copies</small></div><div><strong>{ownAccounts(current.id).length}</strong><small>Own accounts</small></div></div>
        {#if current.error}<ErrorState title="Some listener details are unavailable" detail={current.error} onretry={load} />{/if}
        <section class="settings-card" aria-label="Recent plays"><header><h3>Recent plays</h3></header>{#each current.recentPlays ?? [] as play}<ItemRow title={play.title || "Title unavailable"} subtitle={`${play.artist || "Artist unavailable"} · ${lastActive(play.playedAt)}`} />{:else}<EmptyState title="No plays yet" description="This listener’s recent plays appear here." />{/each}</section>
        {#if policyError}<ErrorState title="Library approval settings could not be loaded" detail={policyError} onretry={loadPolicy} />{/if}
        <section class="settings-card"><div class="settings-row"><div><strong>Clean only, locked</strong><small>This listener can't turn explicit songs back on.</small></div><Switch aria-label="Lock clean only" checked={current.preferences?.cleanOnlyLocked ?? false} disabled={Boolean(action) || !current.preferences} onchange={event => void toggle(current!, event.currentTarget.checked)} /></div>
        {#if household?.addWithoutApproval === "admins-and-trusted-listeners"}<div class="settings-row"><div><strong>Adds to the library without approval</strong><small>Follows the household's approval setting.</small></div><Switch aria-label="Adds to the library without approval" checked={current.isTrustedListener ?? false} disabled={Boolean(action) || fetching} onchange={event => void setTrust(current!, event.currentTarget.checked)} /></div>{/if}
        <div class="settings-row"><div><strong>Can connect their own accounts</strong><small>Follows the household setting.</small></div><span>{ownConnections === null ? "Not provided" : ownConnections ? "Household · On" : "Household · Off"}</span></div><div class="settings-row"><div><strong>Playlist editing</strong><small>Each listener allows it in Sources › Accounts.</small></div><StatePill tone={current.playlistEditingAllowed ? "ok" : "neutral"} text={current.playlistEditingAllowed ? "Allowed" : "Not yet"} /></div></section>
        <section class="settings-card" aria-label="Playlist copies"><header><h3>Playlist copies</h3></header>
          {#if current.copies === null}<ErrorState title="Personal copies could not be loaded" onretry={load} />
          {:else if !current.copies.length}<EmptyState title="No personal copies" description="A listener's own versions of playlists appear here." />
          {:else}{#each current.copies as copy (copy.id)}<ItemRow title={copy.name} subtitle={`From ${originalCopyName(copy, links)}`} />{/each}{/if}
        </section>
        {#if ownAccounts(current.id).length}<section class="settings-card"><header><h3>Own accounts</h3></header>{#each ownAccounts(current.id) as account (account.id)}<ItemRow title={account.displayName} subtitle={account.providerId} />{/each}</section>{/if}
      </div>
      {#snippet footer()}<Button href={`#/activity?view=plays&user=${encodeURIComponent(current.id)}`} onclick={() => rememberActivityScope("everyone")}>See their activity</Button>{/snippet}
    </SidePanel>
  {/await}
{/if}

<style>
  .listener-table td:first-child { white-space: nowrap; }
  .listener-details .settings-card .settings-row { grid-template-columns: minmax(0, 1fr) auto; font-size: var(--text-sm); }
  .listener-details :global(.empty-state) { min-height: 0; padding: var(--space-4); }
  .listener-kpis { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-2); }
  .listener-kpis > div { display: grid; gap: var(--space-1); border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); }
  .listener-kpis strong { font-size: var(--text-xl); font-variant-numeric: tabular-nums; }
  .listener-kpis small { color: var(--color-ink-muted); font-size: var(--text-xs); }
</style>
