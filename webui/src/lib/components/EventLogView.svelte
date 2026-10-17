<script lang="ts">
  import { onMount } from "svelte";
  import { readActivityScope, rememberActivityScope, type ActivityScope } from "$lib/activity-scope";
  import { Button } from "$lib/components/ui/button";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import EventHistoryFeed from "$lib/components/EventHistoryFeed.svelte";
  import ActivityJobs from "$lib/components/ActivityJobs.svelte";
  import ActivityTasks from "$lib/components/ActivityTasks.svelte";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { administrator = false }: { administrator?: boolean } = $props();
  let view = $state("history");
  let scope = $state<ActivityScope>("mine");
  let ready = $state(false);
  let attention = $state(false);
  let everyoneButton = $state<HTMLButtonElement>();
  onMount(() => {
    scope = readActivityScope(administrator);
    ready = true;
    return liveUpdates.subscribe((update) => {
      if (administrator && scope === "mine" && update.requiresAdministrator) attention = true;
    });
  });
  function chooseScope(next: ActivityScope) {
    scope = next;
    attention = false;
    rememberActivityScope(next);
    liveUpdates.setScope(next);
  }
</script>

<SegmentedNav label="Activity views" items={[
  { id: "history", label: "History" }, { id: "jobs", label: "Jobs" }, { id: "tasks", label: "Tasks" },
]} active={view} onchange={(next) => view = next} />
<div class="activity-toolbar">
  {#if administrator}
    <div class="activity-audience" role="group" aria-label="Activity scope">
      <button type="button" class="filter-chip" aria-pressed={scope === "mine"} onclick={() => chooseScope("mine")}>Mine</button>
      <button bind:this={everyoneButton} type="button" class="filter-chip" aria-pressed={scope === "everyone"} onclick={() => chooseScope("everyone")}>Everyone</button>
    </div>
  {/if}
  <p class="activity-scope">{scope === "everyone" ? "Work across your household" : "Your background work"} · <span role="status" aria-live="polite">{liveUpdates.state.status === "live" ? "Live updates connected" : "Reconnecting to live updates; checking periodically"}</span></p>
</div>
{#if administrator && scope === "mine" && attention}
  <div class="degraded-banner" role="status">
    <p><strong>Household work needs attention.</strong> Open Everyone to review failed background work.</p>
    <Button variant="secondary" onclick={() => { chooseScope("everyone"); everyoneButton?.focus(); }}>Review household work</Button>
  </div>
{/if}
{#if ready}
  {#key `${view}:${scope}`}
    {#if view === "tasks"}
      <ActivityTasks {administrator} {scope} />
    {:else}
      {#if view === "jobs"}<ActivityJobs {administrator} {scope} />{:else}<EventHistoryFeed {administrator} {scope} />{/if}
    {/if}
  {/key}
{/if}

<style>
  .activity-toolbar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .75rem 1rem; margin-block: var(--space-5) var(--section-gap); }
  .activity-audience { display: flex; gap: .5rem; }
  .activity-audience button { min-height: var(--control-md); }
  .activity-scope { margin: 0; color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; }
</style>
