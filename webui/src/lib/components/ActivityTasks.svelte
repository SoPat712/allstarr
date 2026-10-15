<script lang="ts">
  import { onMount } from "svelte";
  import { activityWork, playlistLinks, type ActivityTask } from "$lib/api";
  import { liveUpdates, createRefreshScheduler } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RouteError from "$lib/components/RouteError.svelte";

  let { administrator = false }: { administrator?: boolean } = $props();
  let items = $state<ActivityTask[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let running = $state("");
  let queued = $state(new Set<string>());
  let disposed = false;
  let hasMore = $state(false);

  function time(value?: string | null) {
    if (!value || Number.isNaN(Date.parse(value))) return "Not recorded yet";
    return new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
  async function load() {
    if (busy) return;
    busy = true;
    error = "";
    try {
      const next = await activityWork.tasks();
      if (disposed) return;
      for (const item of next.items) if (items.find((old) => old.id === item.id)?.lastRunAt !== item.lastRunAt) queued.delete(item.id);
      queued = new Set(queued);
      items = next.items;
      hasMore = next.hasMore ?? false;
    } catch { if (!disposed) error = "Scheduled tasks could not be loaded. Try again."; }
    finally { if (!disposed) { loading = false; busy = false; } }
  }
  async function run(task: ActivityTask) {
    if (!task.runPlaylistLinkId || running || queued.has(task.id)) return;
    running = task.id;
    try {
      await playlistLinks.run(task.runPlaylistLinkId);
      if (disposed) return;
      queued = new Set([...queued, task.id]);
      toast.success("Task queued. Follow its progress in Queue.");
      await load();
    } catch { if (!disposed) toast.error("Task could not be queued. Refresh Tasks and try again."); }
    finally { if (!disposed) running = ""; }
  }
  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="panel activity-tasks" aria-label="Background tasks" aria-busy={busy}>
  <header class="tasks-heading"><div><h2>Tasks</h2><p>{administrator ? "Playlist schedules and automatic household maintenance." : "Your recurring playlist updates."}</p></div><Button variant="secondary" disabled={busy} onclick={() => void load()}>Refresh tasks</Button></header>
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading tasks" aria-busy="true" />
  {:else if error && !items.length}<RouteError eyebrow="Tasks unavailable" title="Scheduled tasks are unavailable." message={error} onRetry={load} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error} <Button variant="secondary" onclick={() => void load()}>Retry tasks</Button></p>{/if}
    {#each items as task (task.id)}
      <article class="activity-task">
        <div class="task-copy"><h3>{task.name}</h3><p>{administrator && task.ownerName ? `${task.ownerName} · ` : ""}{task.cadence}</p>{#if task.detail}<p>{task.detail}</p>{/if}</div>
        <Badge state={task.state.toLowerCase()}>{task.state}</Badge>
        <dl><div><dt>Last run</dt><dd>{time(task.lastRunAt)}</dd></div><div><dt>{task.kind === "maintenance" ? "Next check (approximate)" : "Next run"}</dt><dd>{task.enabled ? task.nextRunAt ? time(task.nextRunAt) : task.state === "Running" ? "After this run" : "When needed" : "Paused"}</dd></div></dl>
        {#if task.runPlaylistLinkId}<Button variant="secondary" disabled={Boolean(running) || queued.has(task.id)} onclick={() => void run(task)}>{queued.has(task.id) ? "Queued" : running === task.id ? "Queuing…" : "Run now"}</Button>{:else}<span class="task-automatic">Runs automatically</span>{/if}
      </article>
    {:else}<div class="compact-empty"><strong>No scheduled tasks</strong><p>Choose Keep in sync on a playlist and add a schedule to see its next update here.</p></div>{/each}
    {#if hasMore}<p role="status">Showing the first 500 schedules. Open Library to manage additional playlists.</p>{/if}
  {/if}
</section>

<style>
  .activity-tasks { padding: var(--surface-gutter); min-width: 0; }
  .tasks-heading { display: flex; justify-content: space-between; align-items: start; gap: 1rem; margin-bottom: var(--section-gap); }
  h2, h3, p, dl, dd { margin: 0; } h2 { font-size: 1.125rem; } h3 { font-size: .9375rem; }
  p, dt, dd, .task-automatic { color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .activity-task { border-top: 1px solid var(--md-sys-color-outline-variant); padding-block: 1rem; display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: .75rem 1rem; align-items: start; }
  .task-copy { min-width: 0; overflow-wrap: anywhere; }
  dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .75rem; }
  dt { font-weight: 600; } dd { font-variant-numeric: tabular-nums; }
  .task-automatic { align-self: center; }
  @media (max-width: 650px) { .tasks-heading { display: grid; } .activity-task { grid-template-columns: minmax(0, 1fr); } dl { grid-template-columns: minmax(0, 1fr); } }
</style>
