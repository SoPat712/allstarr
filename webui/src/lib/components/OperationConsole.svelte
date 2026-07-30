<script lang="ts">
  import { onMount } from "svelte";
  import { home, type JobResponse } from "$lib/api";
  import { compactProgress, progressDetails } from "$lib/jobs";
  import { liveUpdates } from "$lib/live-updates.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Popover } from "$lib/components/ui/popover";

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
  let open = $state(false);
  let cancelOpen = $state(false);
  let cancelling = $state(false);
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let observedJobId = "";
  let observedState = "";

  function selectJob(next: JobResponse | null) {
    return next?.jobs.find((item) => item.id === requestedJobId) ??
      next?.jobs.find((item) =>
        item.type === "playlist.materialize" &&
        next.progress.some((progress) =>
          progress.jobId === item.id && progressDetails(progress).playlist === playlistName));
  }

  const job = $derived(selectJob(data));
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
      const next = selectJob(data);
      if (observedJobId === next?.id &&
          observedState && ["Pending", "Running", "Deferred"].includes(observedState) &&
          !["Pending", "Running", "Deferred"].includes(next.state)) {
        await onTerminal();
      }
      if (next) {
        observedJobId = next.id;
        observedState = next.state;
      }
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
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if job}
  <Popover.Root bind:open>
    <Popover.Trigger class="operation-trigger" aria-label={`Operation details: ${latest.stage?.replaceAll(".", " ") || job.type.replaceAll(".", " ")}, ${job.state}`}>
      <span aria-live="polite">{latest.stage?.replaceAll(".", " ") || job.type.replaceAll(".", " ")}</span>
      <span class={`status-pill ${job.state.toLowerCase()}`}>{job.state}</span>
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Content class="bits-menu operation-popover" sideOffset={6} align="end">
        <header>
          <span><strong>{latest.stage?.replaceAll(".", " ") || job.type.replaceAll(".", " ")}</strong><small>{latest.message || job.state}</small></span>
          <span class={`status-pill ${job.state.toLowerCase()}`}>{job.state}</span>
        </header>
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
            {#if (job.attemptCount ?? 0) > 1}<span><small>Retries</small><strong>{(job.attemptCount ?? 1) - 1}</strong></span>{/if}
            {#if job.failureCount}<span><small>Failures</small><strong>{job.failureCount}</strong></span>{/if}
            {#if job.deferralCount}<span><small>Deferrals</small><strong>{job.deferralCount}</strong></span>{/if}
            {#if job.state === "Deferred" && job.availableAt}<span><small>Wait until</small><strong><time datetime={job.availableAt}>{new Date(job.availableAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</time></strong></span>{/if}
          </div>
          {#if job.lastErrorMessage}<p class="notice-error">{job.lastErrorMessage}</p>{/if}
          <ol>
            {#each entries as entry}
              {@const details = progressDetails(entry)}
              <li><span>{details.message || entry.action.replaceAll(".", " ")}</span><small>{details.track || details.deferralReason || entry.outcome}</small></li>
            {/each}
          </ol>
          {#if active && !job.cancellationRequestedAt}
            <button class="button-secondary" type="button" onclick={() => { open = false; cancelOpen = true; }}>Cancel operation</button>
          {/if}
        </div>
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>

  <ConfirmDialog
    bind:open={cancelOpen}
    title="Cancel this operation?"
    description="Completed durable work remains recorded. The worker will stop at its next safe cancellation point."
    confirmLabel="Cancel operation"
    cancelLabel="Keep running"
    disabled={cancelling}
    onConfirm={cancel}
  />
{/if}
