<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount, tick } from "svelte";
  import { CalendarClock } from "@lucide/svelte";
  import { activityWork, playlistLinks, home, type PlaylistLink, type ProviderDefinition, type ActivityTask } from "$lib/api";
  import { liveUpdates, createRefreshScheduler } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import MediaArtwork from "./MediaArtwork.svelte";
  import { providerDisplayName } from "$lib/sources";
  import { plainTaskName } from "./activity-rows";

  let { administrator = false, scope = "mine" }: { administrator?: boolean; scope?: "mine" | "everyone" } = $props();
  let items = $state<ActivityTask[]>([]);
  let links = $state<PlaylistLink[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let running = $state("");
  let queued = $state(new Set<string>());
  const queuedJobs = new Map<string, string>();
  let opener: HTMLElement | null = null;
  let disposed = false;
  let hasMore = $state(false);
  let filter = $state("all");
  let showAll = $state(false);
  let selected = $state<ActivityTask | null>(null);
  let panelModule = $state<typeof import("./ActivityTaskPanel.svelte")>();
  const isPlaylist = (task: ActivityTask) => Boolean(task.runPlaylistLinkId);
  const filtered = $derived(items.filter(task => filter === "all" || (filter === "paused" ? !task.enabled : task.enabled)));
  const playlists = $derived(filtered.filter(isPlaylist));
  const upkeep = $derived(filtered.filter(task => !isPlaylist(task)));
  const next = $derived(items.filter(task => task.enabled && task.nextRunAt).toSorted((a, b) => a.nextRunAt!.localeCompare(b.nextRunAt!))[0]);

  function time(value?: string | null) {
    if (!value || Number.isNaN(Date.parse(value))) return "Not recorded";
    return new Date(value).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }
  async function load() {
    if (busy) return;
    busy = true; error = "";
    try {
      const next = await activityWork.tasks(scope);
      const completed = await Promise.all([...queuedJobs].map(async ([taskId, jobId]) => {
        try {
          const detail = await activityWork.detail(jobId);
          return ["Succeeded", "Failed", "Cancelled"].includes(detail.job?.state) ? taskId : null;
        } catch { return null; }
      }));
      if (disposed) return;
      for (const item of next.items) if (items.find(old => old.id === item.id)?.lastRunAt !== item.lastRunAt) { queued.delete(item.id); queuedJobs.delete(item.id); }
      for (const id of completed) if (id) { queued.delete(id); queuedJobs.delete(id); }
      queued = new Set(queued);
      items = next.items;
      hasMore = next.hasMore ?? false;
      if (selected) selected = items.find(item => item.id === selected?.id) ?? selected;
    } catch { if (!disposed) error = "Scheduled tasks could not be loaded."; }
    finally { if (!disposed) { loading = false; busy = false; } }
  }
  async function open(task: ActivityTask) {
    opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    selected = task;
    try { panelModule ??= await import("./ActivityTaskPanel.svelte"); }
    catch { toast.error("Task details could not be opened. Try again."); }
  }
  async function run(task: ActivityTask) {
    if (!task.runPlaylistLinkId || running || queued.has(task.id)) return;
    running = task.id;
    try {
      const result = await playlistLinks.run(task.runPlaylistLinkId);
      if (disposed) return;
      queuedJobs.set(task.id, result.jobId);
      queued = new Set([...queued, task.id]);
      toast.success("Task queued. Follow its progress in Jobs.");
      await load();
    } catch { if (!disposed) toast.error("Task could not be queued. Refresh Tasks and try again."); }
    finally { if (!disposed) running = ""; }
  }
  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    void playlistLinks.list().then(result => { if (!disposed) links = result.playlistLinks ?? []; }).catch(() => {});
    void home.schema().then(result => { if (!disposed) providers = result.providers ?? []; }).catch(() => {});
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="activity-tasks" aria-label="Background tasks" aria-busy={busy}>
  <PageStatus label="Tasks status" items={[
    { label: loading ? "Loading tasks…" : items.filter(task => task.enabled).length + " scheduled" },
    { label: next ? "Next: " + plainTaskName(next.name, isPlaylist(next) ? "playlist" : next.kind) + " · " + time(next.nextRunAt) : "Work runs when needed" },
  ]} />
  <div class="tasks-heading">
    <FilterSegments label="Task filters" storageKey={"activity.tasks." + scope} bind:value={filter} options={[
      { id: "all", label: "All", count: items.length },
      { id: "scheduled", label: "Scheduled", count: items.filter(task => task.enabled).length },
      { id: "paused", label: "Paused", count: items.filter(task => !task.enabled).length },
    ]} />
    <RefreshButton variant="outline" onrefresh={load} label="Refresh tasks" />
  </div>
  {#if loading}<RowsSkeleton label="Loading tasks" />
  {:else if error && !items.length}<ErrorState title={error} label="Tasks unavailable" onretry={load} />
  {:else}
    {#if error}<ErrorState title={error} label="Tasks unavailable" onretry={load} />{/if}
    {#if filtered.length}
      <div class="tasks-table"><table>
        <thead><tr><th>Task</th><th>When</th><th>Last run</th><th>Next</th><th><span class="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {#each [{ name: "Playlist syncs", tasks: playlists }, { name: "Upkeep", tasks: upkeep }] as group}
            {#if group.tasks.length}
              <tr class="kind-row"><th colspan="5" scope="rowgroup">{group.name} · {group.tasks.length}</th></tr>
              {#each group.name === "Playlist syncs" && !showAll ? group.tasks.slice(0, 4) : group.tasks as task (task.id)}
                {@const playlist = links.find(link => link.id === task.runPlaylistLinkId)}
                <tr class="activity-task" class:selected={selected?.id === task.id}>
                  <td><button class="task-open" type="button" onclick={() => void open(task)}><MediaArtwork url={playlist?.artworkUrl} fallback="↻" /><span><strong>{plainTaskName(task.name, isPlaylist(task) ? "playlist" : task.kind)}</strong><small>{[playlist ? providerDisplayName(providers, playlist.sourceProviderId, "Playlist sync") : isPlaylist(task) ? "Playlist sync" : "Automatic upkeep", administrator ? task.ownerName : ""].filter(Boolean).join(" · ")}</small></span></button></td>
                  <td class="cadence">{task.cadence}</td>
                  <td><span class="last-run">{time(task.lastRunAt)}</span><ActivityOutcome state={task.state} label={task.state} /></td>
                  <td>{task.enabled ? task.nextRunAt ? time(task.nextRunAt) : task.state === "Running" ? "After this run" : "When needed" : "Paused"}</td>
                  <td>{#if task.runPlaylistLinkId}<Button variant="outline" size="sm" disabled={Boolean(running) || queued.has(task.id)} onclick={() => void run(task)}>{queued.has(task.id) ? "Queued" : running === task.id ? "Queuing…" : "Run now"}</Button>{:else}<Button variant="ghost" size="sm" onclick={() => void open(task)}>Details</Button>{/if}</td>
                </tr>
              {/each}
              {#if group.name === "Playlist syncs" && group.tasks.length > 4}<tr><td colspan="5"><Button variant="ghost" onclick={() => showAll = !showAll}>{showAll ? "Show fewer playlist syncs" : "Show " + countLabel(group.tasks.length - 4, "more playlist sync")}</Button></td></tr>{/if}
            {/if}
          {/each}
        </tbody>
      </table></div>
    {:else}<EmptyState title={items.length ? "No tasks match this filter" : "No scheduled tasks"} description="Choose Keep in sync on a playlist and add a schedule to see its next update here.">{#snippet icon()}<CalendarClock size={24} />{/snippet}</EmptyState>{/if}
    {#if hasMore}<p role="status">Showing the first 500 schedules. Open Library to manage additional playlists.</p>{/if}
  {/if}
</section>
{#if panelModule && selected}
  {@const Panel = panelModule.default}
  {#key selected.id}<Panel task={selected} name={plainTaskName(selected.name, isPlaylist(selected) ? "playlist" : selected.kind)} {providers} onclose={() => { selected = null; void tick().then(() => { if (opener?.isConnected) opener.focus({ preventScroll: true }); }); }} onchanged={load} onrun={() => run(selected!)} running={Boolean(running) || queued.has(selected.id)} />{/key}
{/if}

<style>
  .activity-tasks { container-type: inline-size; display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--space-4); min-width: 0; }
  .tasks-heading { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: var(--space-3); }
  .tasks-table { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  table { width: 100%; border-collapse: collapse; table-layout: fixed; font-size: var(--text-sm); }
  th { text-align: left; color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 500; }
  th, td { padding: var(--space-2) var(--space-3); border-bottom: 1px solid var(--color-edge); overflow-wrap: anywhere; }
  thead th:first-child { width: 34%; } thead th:nth-child(2) { width: 20%; } thead th:last-child { width: 100px; }
  td { background: var(--color-panel); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .kind-row th { background: var(--color-panel-raised); font-weight: 600; }
  .task-open { display: flex; gap: var(--space-3); width: 100%; min-height: 44px; align-items: center; border: 0; background: none; color: var(--color-ink); text-align: left; cursor: pointer; padding: 0; }
  .task-open :global(.media-art) { width: 32px; height: 32px; flex: none; }
  .task-open :global(svg) { flex: none; color: var(--color-ink-muted); }
  .task-open span { min-width: 0; } strong, small, .last-run { display: block; } strong { font-size: var(--text-sm); font-weight: 600; } small { margin-top: var(--space-1); color: var(--color-ink-muted); }
  .last-run { margin-bottom: var(--space-1); font-variant-numeric: tabular-nums; }
  tr.activity-task { position: relative; }
  .task-open::after { content: ""; position: absolute; inset: 0; }
  .task-open:focus-visible { outline: none; }
  .activity-task:has(.task-open:focus-visible) { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  td:last-child :global(button) { position: relative; z-index: 1; }
  .selected td, .activity-task:hover td { background: var(--color-panel-raised); }
  @container (max-width: 650px) { thead { display: none; } table, tbody { display: block; } tr { display: grid; grid-template-columns: minmax(0, 1fr) auto; } .kind-row { display: block; } th { display: block; } td { border: 0; } tr.activity-task { padding: var(--space-2); border-bottom: 1px solid var(--color-edge); background: var(--color-panel); } td:first-child { grid-column: 1 / -1; } td.cadence { grid-column: 1 / -1; padding-top: 0; } td:nth-child(3) { grid-column: 1; } td:nth-child(4) { grid-column: 1; } td:nth-child(4)::before { content: "Next: "; } td:last-child { grid-column: 2; grid-row: 3 / 5; align-self: center; } td[colspan] { grid-column: 1 / -1; } }
</style>
