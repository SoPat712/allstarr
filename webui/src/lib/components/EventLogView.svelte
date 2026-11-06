<script lang="ts">
  import { onMount, tick } from "svelte";
  import { readActivityScope, rememberActivityScope, type ActivityScope } from "$lib/activity-scope";
  import { Button } from "$lib/components/ui/button";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import EventHistoryFeed from "$lib/components/EventHistoryFeed.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { administrator = false, currentUserId = "", initialView = "history", initialReportId = "", initialJobId = "" }: { administrator?: boolean; currentUserId?: string; initialView?: string; initialReportId?: string; initialJobId?: string } = $props();
  let view = $state("history");
  let scope = $state<ActivityScope>("mine");
  let ready = $state(false);
  let attention = $state(false);
  let mobile = $state(false);
  let mineButton = $state<HTMLButtonElement>();
  let everyoneButton = $state<HTMLButtonElement>();
  let scopeFocus = $state<ActivityScope | null>(null);
  $effect(() => { view = ["history", "plays", ...(administrator ? ["jobs", "tasks", "reports"] : [])].includes(initialView) ? initialView : "history"; });
  onMount(() => {
    const breakpoint = matchMedia("(max-width: 650px)");
    const syncMobile = () => mobile = breakpoint.matches;
    syncMobile();
    breakpoint.addEventListener("change", syncMobile);
    scope = readActivityScope(administrator);
    ready = true;
    const unsubscribe = liveUpdates.subscribe((update) => {
      if (administrator && scope === "mine" && update.requiresAdministrator) attention = true;
    });
    return () => { unsubscribe(); breakpoint.removeEventListener("change", syncMobile); };
  });
  async function chooseScope(next: ActivityScope) {
    scopeFocus = next;
    scope = next;
    attention = false;
    rememberActivityScope(next);
    liveUpdates.setScope(next);
    await tick();
    const button = next === "mine" ? mineButton : everyoneButton;
    if (button?.isConnected && (!mobile || view !== "history")) { button.focus(); scopeFocus = null; }
  }
  function restoreScopeFocus(button: HTMLButtonElement, id: ActivityScope) {
    void tick().then(() => {
      if (scopeFocus === id && button.isConnected) {
        button.focus();
        if (!mobile || view !== "history" || button.closest(".event-log-chip-row")) scopeFocus = null;
      }
    });
  }
</script>

{#snippet scopeControls()}
  <div class="seg activity-audience" role="group" aria-label="Activity scope">
    <button bind:this={mineButton} use:restoreScopeFocus={"mine"} type="button" aria-pressed={scope === "mine"} onclick={() => void chooseScope("mine")}>Mine</button>
    <button bind:this={everyoneButton} use:restoreScopeFocus={"everyone"} type="button" aria-pressed={scope === "everyone"} onclick={() => void chooseScope("everyone")}>Everyone</button>
  </div>
{/snippet}

<div class="activity-content">
<SegmentedNav label="Activity views" items={[
  { id: "history", label: "History" }, { id: "plays", label: "Plays" },
  ...(administrator ? [{ id: "jobs", label: "Jobs" }, { id: "tasks", label: "Tasks" }, { id: "reports", label: "Reports" }] : []),
]} active={view} onchange={(next) => { scopeFocus = null; view = next; }} />
{#if view !== "reports"}
<div class="activity-toolbar">
  {#if administrator && (!mobile || view !== "history")}{@render scopeControls()}{/if}
  <p class="activity-scope">{scope === "everyone" ? "Your household’s activity" : "Your activity"} · <span role="status" aria-live="polite">{liveUpdates.state.status === "live" ? "Live updates connected" : "Reconnecting to live updates; checking periodically"}</span></p>
</div>
{/if}
{#if view !== "reports" && administrator && scope === "mine" && attention}
  <div class="degraded-banner" role="status">
    <p><strong>Household work needs attention.</strong> Open Everyone to review failed background work.</p>
    <Button variant="secondary" onclick={() => void chooseScope("everyone")}>Review household work</Button>
  </div>
{/if}
{#if ready}
  {#key `${view}:${scope}:${view === "reports" ? initialReportId : view === "jobs" ? initialJobId : ""}`}
    {#if view === "reports" && administrator}
      {#await import("./ProblemReportsInbox.svelte")}<RowsSkeleton label="Loading reports" />{:then module}{@const View = module.default}<View {administrator} {initialReportId} />{:catch}<ErrorState title="Reports could not be opened." onretry={() => window.location.reload()} />{/await}
    {:else if view === "tasks"}
      {#await import("./ActivityTasks.svelte")}<RowsSkeleton label="Loading tasks" />{:then module}{@const View = module.default}<View {administrator} {scope} />{:catch}<ErrorState title="Tasks could not be opened." onretry={() => window.location.reload()} />{/await}
    {:else if view === "plays"}
      {#await import("./ActivityPlays.svelte")}<RowsSkeleton label="Loading plays" />{:then module}{@const View = module.default}<View {administrator} {scope} {currentUserId} />{:catch}<ErrorState title="Plays could not be opened." onretry={() => window.location.reload()} />{/await}
    {:else}
      {#if view === "jobs"}{#await import("./ActivityJobs.svelte")}<RowsSkeleton label="Loading jobs" />{:then module}{@const View = module.default}<View {administrator} {scope} {initialJobId} />{:catch}<ErrorState title="Jobs could not be opened." onretry={() => window.location.reload()} />{/await}{:else}<EventHistoryFeed {administrator} {scope} scopeControls={administrator && mobile ? scopeControls : undefined} />{/if}
    {/if}
  {/key}
{/if}

</div>

<style>
  .activity-content { min-width: 0; }
  .activity-content :global(:is(button, [data-slot="button"])), :global(body:has(.activity-content) [data-side-panel] :is(button, [data-slot="button"])) { min-width: 44px; min-height: 44px; }
  .activity-content :global(.event-summary-copy > :is(strong, small)) { white-space: normal; overflow-wrap: anywhere; }
  .activity-content :global(:is(.event-kind-icon[data-tone], .activity-outcome)) {
    color: var(--activity-ink, var(--md-sys-color-on-surface-variant));
    background: var(--activity-container, var(--md-sys-color-surface-container-highest));
  }
  .activity-content :global(.activity-outcome) { max-width: 100%; gap: .35rem; border-color: transparent; padding-block: .25rem; line-height: 1.25; white-space: normal; }
  .activity-content :global(.activity-outcome svg) { flex: none; }
  .activity-content :global(:is([data-tone="success"], .badge-success)) { --activity-ink: var(--on-success-container); --activity-container: var(--success-container); }
  .activity-content :global(:is([data-tone="accent"], .badge-accent)) { --activity-ink: var(--md-sys-color-on-primary-container); --activity-container: var(--md-sys-color-primary-container); }
  .activity-content :global(:is([data-tone="warning"], .badge-warning)) { --activity-ink: var(--on-warning-container); --activity-container: var(--warning-container); }
  .activity-content :global(:is([data-tone="danger"], .badge-danger)) { --activity-ink: var(--md-sys-color-on-error-container); --activity-container: var(--md-sys-color-error-container); }

  :global(body:has([data-side-panel])) .activity-content { margin-right: var(--side-panel-offset, 0px); }
  .activity-toolbar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .75rem 1rem; margin-block: var(--space-5) var(--section-gap); }
  .activity-content :global(.activity-audience button) { min-height: var(--control-md); }
  .activity-scope { margin: 0; color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; }
</style>
