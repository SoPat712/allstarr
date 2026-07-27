<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog } from "bits-ui";
  import { home, type JobResponse } from "$lib/api";
  import { compactProgress, progressDetails } from "$lib/jobs";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let {
    playlistName,
    requestedJobId = "",
    onTerminal,
  }: {
    playlistName: string;
    requestedJobId?: string;
    onTerminal: () => void | Promise<void>;
  } = $props();

  let data = $state<JobResponse | null>(null);
  let cancelOpen = $state(false);
  let cancelling = $state(false);
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let observedState = "";

  const job = $derived(
    data?.jobs.find((item) => item.id === requestedJobId) ??
    data?.jobs.find((item) =>
      item.type === "playlist.materialize" &&
      data?.progress.some((progress) =>
        progress.jobId === item.id && progressDetails(progress).playlist === playlistName)),
  );
  const entries = $derived(compactProgress(
    data?.progress.filter((item) => item.jobId === job?.id) ?? [],
  ));
  const latest = $derived(entries[0] ? progressDetails(entries[0]) : {});
  const active = $derived(job && ["Pending", "Running", "Deferred"].includes(job.state));
  const percent = $derived(
    latest.total ? Math.min(100, Math.round(((latest.completed ?? 0) / latest.total) * 100)) : null,
  );

  async function load() {
    try {
      data = await home.jobs();
      const next = data.jobs.find((item) => item.id === requestedJobId)?.state;
      if (observedState && ["Pending", "Running", "Deferred"].includes(observedState) &&
          next && !["Pending", "Running", "Deferred"].includes(next)) {
        await onTerminal();
      }
      if (next) observedState = next;
    } catch {
      // The parent route retains its authoritative playlist state when job diagnostics fail.
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void load();
    }, 250);
  }

  async function cancel() {
    if (!job || cancelling) return;
    cancelling = true;
    try {
      await home.cancelJob(job.id);
      cancelOpen = false;
      await load();
    } finally {
      cancelling = false;
    }
  }

  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    const fallback = setInterval(() => {
      if (liveUpdates.state.status !== "live") void load();
    }, 5_000);
    return () => {
      unsubscribe();
      clearInterval(fallback);
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if job}
  <details class="operation-console" open={Boolean(active)}>
    <summary>
      <span><strong>{latest.stage?.replaceAll(".", " ") || job.type.replaceAll(".", " ")}</strong><small>{latest.message || job.state}</small></span>
      <span class={`status-pill ${job.state.toLowerCase()}`}>{job.state}</span>
    </summary>
    <div class="operation-console-body">
      {#if percent !== null}
        <div class="operation-progress" aria-label={`${percent}% complete`}><span style={`width:${percent}%`}></span></div>
      {/if}
      <div class="operation-facts">
        {#if latest.playlist}<span><small>Playlist</small><strong>{latest.playlist}</strong></span>{/if}
        {#if latest.track}<span><small>Track</small><strong>{latest.track}</strong></span>{/if}
        {#if latest.provider}<span><small>Provider</small><strong>{latest.provider}</strong></span>{/if}
        {#if latest.total != null}<span><small>Progress</small><strong>{latest.completed ?? 0}/{latest.total}</strong></span>{/if}
        {#if latest.throughputPerSecond != null}<span><small>Throughput</small><strong>{latest.throughputPerSecond.toFixed(1)}/s</strong></span>{/if}
        {#if job.failureCount}<span><small>Failures</small><strong>{job.failureCount}</strong></span>{/if}
        {#if job.deferralCount}<span><small>Deferrals</small><strong>{job.deferralCount}</strong></span>{/if}
      </div>
      {#if job.lastErrorMessage}<p class="notice-error">{job.lastErrorMessage}</p>{/if}
      <ol>
        {#each entries as entry}
          {@const details = progressDetails(entry)}
          <li><span>{details.message || entry.action.replaceAll(".", " ")}</span><small>{details.track || details.deferralReason || entry.outcome}</small></li>
        {/each}
      </ol>
      {#if active && !job.cancellationRequestedAt}
        <button class="button-secondary" type="button" onclick={() => cancelOpen = true}>Cancel operation</button>
      {/if}
    </div>
  </details>

  <AlertDialog.Root bind:open={cancelOpen}>
    <AlertDialog.Portal>
      <AlertDialog.Overlay class="dialog-overlay" />
      <AlertDialog.Content class="confirm-dialog">
        <AlertDialog.Title>Cancel this operation?</AlertDialog.Title>
        <AlertDialog.Description>Completed durable work remains recorded. The worker will stop at its next safe cancellation point.</AlertDialog.Description>
        <footer>
          <AlertDialog.Cancel class="button-secondary">Keep running</AlertDialog.Cancel>
          <AlertDialog.Action class="button-danger" disabled={cancelling} onclick={() => void cancel()}>Cancel operation</AlertDialog.Action>
        </footer>
      </AlertDialog.Content>
    </AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
