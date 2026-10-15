<script lang="ts">
  import { onMount, tick, untrack } from "svelte";
  import { activityWork, home, type Job, type JobDetail, type JobProgress } from "$lib/api";
  import { jobName, jobNames, jobState, progressPercent, outcomeClass } from "$lib/activity";
  import { progressDetails } from "$lib/jobs";
  import { liveUpdates, createRefreshScheduler } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SelectField from "$lib/components/SelectField.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";

  let { mode, administrator = false }: { mode: "queue" | "history"; administrator?: boolean } = $props();
  let items = $state<Job[]>([]);
  let progress = $state<JobProgress[]>([]);
  let mounted = $state(false);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let stateFilter = $state("");
  let typeFilter = $state("");
  let cursor = $state("");
  let cursorId = $state("");
  let hasMore = $state(false);
  let expanded = $state("");
  let detail = $state<JobDetail | null>(null);
  let detailBusy = $state(false);
  let detailError = $state("");
  let cancelJob = $state<Job | null>(null);
  let cancelOpen = $state(false);
  let cancelling = $state(false);
  let cancelError = $state("");
  let refreshButton = $state<HTMLButtonElement | null>(null);
  let request = 0;
  let disposed = false;
  let refreshPending = false;
  const stateOptions = $derived((mode === "queue" ? ["Pending", "Running", "RetryScheduled", "Deferred"] : ["Succeeded", "Failed", "Cancelled"])
    .map((value) => ({ value, label: jobState(value) })));

  function time(value?: string | null) {
    if (!value || Number.isNaN(Date.parse(value))) return "Not recorded";
    return new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }

  async function load(kind: "replace" | "refresh" | "older" = "refresh") {
    if (busy && kind !== "replace") { if (kind === "refresh") refreshPending = true; return; }
    const version = ++request;
    busy = true;
    error = "";
    try {
      const next = await activityWork.jobs(mode, stateFilter, typeFilter, kind === "older" ? cursor : "", kind === "older" ? cursorId : "");
      if (disposed || version !== request) return;
      if (kind === "replace" || mode === "queue" && kind === "refresh") items = next.jobs;
      else {
        const merged = new Map(items.map((job) => [job.id, job]));
        for (const job of next.jobs) merged.set(job.id, job);
        items = [...merged.values()].sort((a, b) => (b.completedAt ?? b.createdAt ?? b.updatedAt).localeCompare(a.completedAt ?? a.createdAt ?? a.updatedAt) || b.id.localeCompare(a.id));
      }
      progress = kind === "older" ? [...next.progress, ...progress] : next.progress;
      if (kind !== "refresh" || mode === "queue" || !cursor) {
        hasMore = next.hasMore ?? false;
        cursor = next.nextCursor ?? "";
        cursorId = next.nextCursorId ?? "";
      }
      if (expanded && !items.some((job) => job.id === expanded)) expanded = "";
    } catch {
      if (!disposed && version === request) error = "Background work could not be loaded. Try again.";
    } finally {
      if (!disposed && version === request) {
        loading = false;
        busy = false;
        if (refreshPending) { refreshPending = false; scheduler.schedule(); }
      }
    }
  }
  const scheduler = createRefreshScheduler(() => load());

  $effect(() => {
    stateFilter; typeFilter;
    if (mounted) untrack(() => { expanded = ""; detail = null; void load("replace"); });
  });

  async function attempts(job: Job, older = false) {
    if (!older && expanded === job.id) { expanded = ""; return; }
    expanded = job.id;
    if (!older) detail = null;
    detailBusy = true;
    detailError = "";
    try {
      const next = await activityWork.detail(job.id, older ? detail?.nextCursor ?? "" : "", older ? detail?.nextCursorId ?? "" : "");
      if (disposed || expanded !== job.id) return;
      detail = older && detail ? { ...next, attempts: [...detail.attempts, ...next.attempts].filter((item, index, all) => all.findIndex((candidate) => candidate.attemptNumber === item.attemptNumber) === index) } : next;
    } catch {
      if (!disposed && expanded === job.id) detailError = "Attempt history could not be loaded. Try again.";
    } finally { if (!disposed && expanded === job.id) detailBusy = false; }
  }

  async function cancel() {
    if (!cancelJob || cancelling) return;
    cancelling = true;
    cancelError = "";
    try {
      await home.cancelJob(cancelJob.id);
      cancelOpen = false;
      toast.success("Cancellation requested. The worker stops at its next safe point.");
      await load("replace");
      await tick();
      refreshButton?.focus();
    } catch { cancelError = "The job changed or cancellation failed. Refresh jobs and try again."; }
    finally { cancelling = false; }
  }

  onMount(() => {
    mounted = true;
    const unsubscribe = liveUpdates.subscribe(() => { scheduler.schedule(); if (expanded && !detailBusy) { const job = items.find((item) => item.id === expanded); if (job) void attempts(job, true); } });
    return () => { disposed = true; request++; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="panel activity-work" aria-label={mode === "queue" ? "Job queue" : "Finished jobs"} aria-busy={busy}>
  <header class="panel-heading">
    <div><h2>{mode === "queue" ? "Queue" : "Finished jobs"}</h2><p>{mode === "queue" ? "Waiting, running and scheduled retries." : "Completed, failed and cancelled work, with its recorded attempts."}</p></div>
    <Button bind:ref={refreshButton} variant="secondary" disabled={busy} onclick={() => void load()}>Refresh jobs</Button>
  </header>
  <div class="activity-filters">
    <SelectField label="Job state" bind:value={stateFilter} options={[{ value: "", label: "All states" }, ...stateOptions]} />
    <SelectField label="Job type" bind:value={typeFilter} options={[{ value: "", label: "All work" }, ...Object.entries(jobNames).map(([value, label]) => ({ value, label }))]} />
  </div>
  {#if loading}
    <Skeleton class="skeleton-panel" aria-label="Loading jobs" aria-busy="true" />
  {:else if error && !items.length}
    <RouteError eyebrow="Jobs unavailable" title="Background work is unavailable." message={error} onRetry={() => load("replace")} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error} <Button variant="secondary" onclick={() => void load()}>Retry jobs</Button></p>{/if}
    <p class="sr-only" role="status" aria-live="polite">{items.length} {mode === "queue" ? "queued" : "finished"} jobs loaded.</p>
    <div class="activity-job-list">
      {#each items as job (job.id)}
        {@const latest = progress.find((entry) => entry.jobId === job.id)}
        {@const info = latest ? progressDetails(latest) : {}}
        {@const percent = progressPercent(info.completed, info.total)}
        <article class="activity-job" data-job-id={job.id}>
          <div class="activity-job-heading">
            <div><h3>{jobName(job.type)}</h3><p>{administrator ? `${job.ownerName || "Household"} · ` : ""}{time(job.completedAt ?? job.createdAt ?? job.updatedAt)}</p></div>
            <Badge state={outcomeClass(job.state)}>{job.cancellationRequestedAt ? "Cancellation requested" : jobState(job.state)}</Badge>
          </div>
          {#if info.message}<p>{info.message}</p>{/if}
          {#if info.playlist || info.track}<p>{[info.playlist, info.track].filter(Boolean).join(" · ")}</p>{/if}
          {#if percent !== null}<Progress value={percent} aria-label={`${percent}% complete`} /><p>{info.completed} of {info.total} · {percent}%</p>{/if}
          {#if ["RetryScheduled", "Deferred", "Pending"].includes(job.state) && job.availableAt}<p>{job.state === "Pending" ? "Available" : "Next attempt"}: <time datetime={job.availableAt}>{time(job.availableAt)}</time></p>{/if}
          {#if job.lastErrorMessage}<p class="notice-error">{job.lastErrorMessage}</p>{/if}
          <div class="activity-job-actions">
            <Button variant="secondary" aria-expanded={expanded === job.id} onclick={() => void attempts(job)}>{expanded === job.id ? "Hide attempts" : "Show attempts"}</Button>
            {#if mode === "queue" && job.canCancel && !job.cancellationRequestedAt}<Button variant="secondary" onclick={() => { cancelJob = job; cancelError = ""; cancelOpen = true; }}>Cancel job</Button>{/if}
          </div>
          {#if expanded === job.id}
            <section class="activity-attempts" aria-label="Attempt history" aria-busy={detailBusy}>
              <h4>Attempt history</h4>
              {#if detailError}<p class="notice-error" role="alert">{detailError}</p><Button variant="secondary" onclick={() => { expanded = ""; void attempts(job); }}>Retry history</Button>{/if}
              {#if !detail && detailBusy}<p role="status">Loading attempts…</p>{/if}
              {#each detail?.attempts ?? [] as attempt (attempt.attemptNumber)}
                <article><strong>Attempt {attempt.attemptNumber} · {jobState(attempt.outcome ?? "In progress")}</strong><p>{time(attempt.startedAt)} → {time(attempt.completedAt)}</p>{#if attempt.errorMessage}<p>{attempt.errorMessage}</p>{/if}</article>
              {:else}
                {#if detail}<p>No retained attempts. {job.attemptCount ?? 0} attempts are recorded on this job{job.lastAttemptOutcome ? `; latest outcome: ${jobState(job.lastAttemptOutcome)}` : ""}.</p>{/if}
              {/each}
              {#if detail?.hasMore}<Button variant="secondary" disabled={detailBusy} onclick={() => void attempts(job, true)}>Load earlier attempts</Button>{/if}
            </section>
          {/if}
        </article>
      {:else}
        <div class="compact-empty"><strong>{stateFilter || typeFilter ? "No jobs match these filters" : mode === "queue" ? "Nothing queued" : "No finished jobs yet"}</strong><p>{mode === "queue" ? "New background work will appear here automatically." : "Finished work and its attempts will appear here."}</p></div>
      {/each}
    </div>
    {#if hasMore}<nav class="activity-pagination" aria-label="Job pages"><Button variant="secondary" disabled={busy} onclick={() => void load("older")}>{mode === "queue" ? "Load more jobs" : "Load earlier jobs"}</Button></nav>{/if}
  {/if}
</section>
<ConfirmDialog bind:open={cancelOpen} title="Cancel this job?" description="Completed work remains recorded. The worker stops at its next safe cancellation point." confirmLabel="Cancel job" cancelLabel="Keep running" disabled={cancelling} error={cancelError} closeOnConfirm={false} onConfirm={cancel} />

<style>
  .activity-work { margin-bottom: var(--section-gap); padding: var(--surface-gutter); min-width: 0; }
  .panel-heading { display: flex; align-items: start; justify-content: space-between; gap: 1rem; }
  h2, h3, h4, p { margin: 0; }
  h2 { font-size: 1.125rem; } h3 { font-size: .9375rem; } h4 { font-size: .875rem; }
  p { color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .activity-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; margin-block: var(--section-gap); }
  .activity-job { padding-block: 1rem; display: grid; gap: .625rem; border-top: 1px solid var(--md-sys-color-outline-variant); min-width: 0; }
  .activity-job-heading { display: flex; justify-content: space-between; gap: .75rem; align-items: start; }
  .activity-job-heading > div { min-width: 0; overflow-wrap: anywhere; }
  .activity-job-actions { display: flex; gap: .5rem; flex-wrap: wrap; }
  .activity-attempts { background: var(--md-sys-color-surface-container); border-radius: 12px; padding: 1rem; display: grid; gap: .75rem; }
  .activity-attempts article { display: grid; gap: .25rem; padding-block: .5rem; border-top: 1px solid var(--md-sys-color-outline-variant); }
  .activity-pagination { display: flex; justify-content: center; padding-top: 1rem; }
  @media (max-width: 650px) { .panel-heading { display: grid; } .activity-filters { grid-template-columns: minmax(0, 1fr); } .activity-job-heading { flex-wrap: wrap; } }
</style>
