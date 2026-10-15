<script lang="ts">
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import EventHistoryFeed from "$lib/components/EventHistoryFeed.svelte";
  import ActivityJobs from "$lib/components/ActivityJobs.svelte";
  import ActivityTasks from "$lib/components/ActivityTasks.svelte";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { administrator = false }: { administrator?: boolean } = $props();
  let view = $state("history");
</script>

<SegmentedNav label="Activity views" items={[
  { id: "queue", label: "Queue" }, { id: "history", label: "History" }, { id: "tasks", label: "Tasks" },
]} active={view} onchange={(next) => view = next} />
<p class="activity-scope">{administrator ? "Work across your household" : "Your background work"} · <span role="status" aria-live="polite">{liveUpdates.state.status === "live" ? "Live updates connected" : "Reconnecting to live updates; checking periodically"}</span></p>
{#if view === "tasks"}
  <ActivityTasks {administrator} />
{:else}
  {#key view}<ActivityJobs mode={view === "queue" ? "queue" : "history"} {administrator} />{/key}
  {#if view === "history"}<EventHistoryFeed {administrator} />{/if}
{/if}

<style>
  .activity-scope { color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; margin-block: 0 var(--section-gap); }
</style>
