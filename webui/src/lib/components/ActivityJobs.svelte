<script lang="ts">
  import { onMount, tick, untrack } from "svelte";
  import { ChevronDown, RefreshCw } from "@lucide/svelte";
  import { activityWork, home, type Job, type JobDetail, type JobProgress } from "$lib/api";
  import { dayLabel, jobName, jobNames, jobState, progressPercent, activityTone, activityText, validJobId } from "$lib/activity";
  import { progressDetails } from "$lib/jobs";
  import { liveUpdates, createRefreshScheduler } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ActivityIcon from "$lib/components/ActivityIcon.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";

  let { administrator = false, scope = "mine", initialJobId = "" }: { administrator?: boolean; scope?: "mine" | "everyone"; initialJobId?: string } = $props();
  const queueStates = ["Pending", "Running", "RetryScheduled", "Deferred"];
  const finishedStates = ["Succeeded", "Failed", "Cancelled"];
  let linkedJob = $state<Job | null>(null);
  let linkedLoading = $state(false);
  let linkedError = $state("");
  let queue = $state<Job[]>([]);
  let finished = $state<Job[]>([]);
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
  const stateOptions = [...queueStates, ...finishedStates].map((value) => ({ value, label: jobState(value) }));
  const wantsQueue = $derived(!stateFilter || queueStates.includes(stateFilter));
  const wantsFinished = $derived(!stateFilter || finishedStates.includes(stateFilter));

  // Routine runs that finished the same way collapse into one row per day.
  const days = $derived.by(() => {
    const result: Array<{ label: string; rows: Job[][] }> = [];
    for (const job of finished.filter(item => item.id !== linkedJob?.id)) {
      const label = dayLabel(finishedAt(job));
      let day = result.at(-1);
      if (day?.label !== label) result.push(day = { label, rows: [] });
      const previous = day.rows.at(-1)?.[0];
      if (previous && !job.lastErrorMessage && !previous.lastErrorMessage && previous.type === job.type &&
          previous.state === job.state && previous.ownerName === job.ownerName && previous.source === job.source &&
          previous.targetName === job.targetName && previous.summary === job.summary && previous.displayName === job.displayName && previous.displayState === job.displayState)
        day.rows.at(-1)!.push(job);
      else day.rows.push([job]);
    }
    return result;
  });

  function finishedAt(job: Job) { return job.completedAt ?? job.createdAt ?? job.updatedAt; }
  function time(value?: string | null) {
    if (!value || Number.isNaN(Date.parse(value))) return "Not recorded";
    return new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
  function clock(value?: string | null) {
    if (!value || Number.isNaN(Date.parse(value))) return "";
    return new Date(value).toLocaleTimeString(undefined, { timeStyle: "short" });
  }
  function kind(type: string) {
    const prefix = type.split(".")[0];
    return ({ playlist: "playlist", library: "library", "track-match": "matching", playback: "scrobble", favorite: "scrobble", recording: "caching" } as Record<string, string>)[prefix] ?? "job";
  }
  function statusLabel(job: Job) {
    const label = ({ partly_checked: "Partly checked", not_checked: "Not checked", passed: "Passed" } as Record<string, string>)[job.displayState ?? ""];
    return label || jobState(job.displayState === "failed" ? "Failed" : job.state);
  }
  function severity(state: string) {
    return state === "Failed" ? "error" : state === "RetryScheduled" || state === "Deferred" ? "warning" : "info";
  }
  function meta(job: Job) {
    return [administrator ? job.ownerName || "Household" : "", job.source].filter(Boolean).join(" · ");
  }

  async function load(kind: "replace" | "refresh" | "older" = "refresh") {
    if (busy && kind !== "replace") { if (kind === "refresh") refreshPending = true; return; }
    const version = ++request;
    busy = true;
    error = "";
    try {
      const [nextQueue, nextFinished] = await Promise.all([
        kind !== "older" && wantsQueue ? activityWork.jobs("queue", stateFilter, typeFilter, "", "", scope) : null,
        wantsFinished ? activityWork.jobs("history", stateFilter, typeFilter, kind === "older" ? cursor : "", kind === "older" ? cursorId : "", scope) : null,
      ]);
      if (disposed || version !== request) return;
      if (kind !== "older") {
        queue = (nextQueue?.jobs ?? []).filter((job) => queueStates.includes(job.state));
        progress = nextQueue?.progress ?? [];
      }
      if (nextFinished) {
        const done = nextFinished.jobs.filter((job) => finishedStates.includes(job.state));
        if (kind === "replace") finished = done;
        else {
          const merged = new Map(finished.map((job) => [job.id, job]));
          for (const job of done) merged.set(job.id, job);
          finished = [...merged.values()].sort((a, b) => finishedAt(b).localeCompare(finishedAt(a)) || b.id.localeCompare(a.id));
        }
        if (kind !== "refresh" || !cursor) {
          hasMore = nextFinished.hasMore ?? false;
          cursor = nextFinished.nextCursor ?? "";
          cursorId = nextFinished.nextCursorId ?? "";
        }
      } else if (kind !== "older") { finished = []; hasMore = false; }
      if (expanded && expanded !== linkedJob?.id && ![...queue, ...finished].some((job) => job.id === expanded)) expanded = "";
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
      if (linkedJob?.id === job.id) linkedJob = next.job;
      detail = older && detail ? { ...next, attempts: [...detail.attempts, ...next.attempts].filter((item, index, all) => all.findIndex((candidate) => candidate.attemptNumber === item.attemptNumber) === index) } : next;
    } catch {
      if (!disposed && expanded === job.id) detailError = "Attempt history could not be loaded. Try again.";
    } finally { if (!disposed && expanded === job.id) detailBusy = false; }
  }

  async function openLinkedJob(id: string) {
    if (!id) return;
    if (!validJobId(id)) { linkedError = "This work reference is not valid."; return; }
    linkedLoading = true; linkedError = "";
    try {
      const next = await activityWork.detail(id);
      if (disposed) return;
      linkedJob = next.job; detail = next; expanded = next.job.id;
      await tick();
      const target = document.getElementById(`job-${next.job.id}`);
      target?.focus({ preventScroll: true });
      target?.scrollIntoView({ block: "nearest" });
    } catch { if (!disposed) linkedError = "This work item is unavailable or you no longer have permission to view it."; }
    finally { if (!disposed) linkedLoading = false; }
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
    if (initialJobId) void openLinkedJob(initialJobId);
    const unsubscribe = liveUpdates.subscribe(() => { scheduler.schedule(); if (expanded && !detailBusy) { const job = [...(linkedJob ? [linkedJob] : []), ...queue, ...finished].find((item) => item.id === expanded); if (job) void attempts(job, true); } });
    return () => { disposed = true; request++; unsubscribe(); scheduler.cancel(); };
  });
</script>

{#snippet attemptHistory(job: Job)}
  <section class="job-attempts" id={`job-${job.id}-attempts`} aria-label="Attempt history" aria-busy={detailBusy}>
    {#if detailError}<p class="notice-error" role="alert">{detailError}</p><Button variant="secondary" onclick={() => { expanded = ""; void attempts(job); }}>Retry history</Button>{/if}
    {#if !detail && detailBusy}<p role="status">Loading attempts…</p>{/if}
    {#each detail?.attempts ?? [] as attempt (attempt.attemptNumber)}
      <article><strong>Attempt {attempt.attemptNumber} · {jobState(attempt.outcome ?? "In progress")}</strong><p>{time(attempt.startedAt)} → {time(attempt.completedAt)}</p>{#if attempt.errorMessage}<p>{attempt.errorMessage}</p>{/if}</article>
    {:else}
      {#if detail}<p>No retained attempts. {job.attemptCount ?? 0} attempts are recorded on this job{job.lastAttemptOutcome ? `; latest outcome: ${jobState(job.lastAttemptOutcome)}` : ""}.</p>{/if}
    {/each}
    {#if detail?.hasMore}<Button variant="secondary" disabled={detailBusy} onclick={() => void attempts(job, true)}>Load earlier attempts</Button>{/if}
  </section>
{/snippet}

{#snippet jobRow(runs: Job[])}
  {@const job = runs[0]}
  {@const latest = progress.find((entry) => entry.jobId === job.id)}
  {@const info = latest ? progressDetails(latest) : {}}
  {@const percent = progressPercent(info.completed, info.total)}
  {@const open = expanded === job.id}
  {@const waiting = ["RetryScheduled", "Deferred", "Pending"].includes(job.state) && job.availableAt}
  <article class="job-row" data-job-id={job.id} id={`job-${job.id}`} tabindex="-1">
    <div class="job-row-line">
      <button type="button" class="job-row-toggle" aria-expanded={open} aria-controls={`job-${job.id}-attempts`} onclick={() => void attempts(job)}>
        <span class="event-kind-icon" data-severity={severity(job.state)} data-tone={activityTone(job.displayState || job.state)} aria-hidden="true"><ActivityIcon kind={kind(job.type)} /></span>
        <span class="job-row-copy">
          <strong>{job.displayName || jobName(job.type)}{runs.length > 1 ? ` · ${runs.length} runs` : ""}</strong>
          {#if meta(job) || waiting}<small>{meta(job)}{meta(job) && waiting ? " · " : ""}{#if waiting}{job.state === "Pending" ? "Available" : "Next attempt"} <time datetime={job.availableAt}>{time(job.availableAt)}</time>{/if}</small>{/if}
          {#if job.targetName}<small class="job-row-context">{activityText(job.targetName)}</small>{/if}
          {#if job.summary}<small class="job-row-context">{activityText(job.summary)}</small>{/if}
          {#if info.message || info.playlist || info.track}<small>{[info.message, info.playlist, info.track].filter(Boolean).join(" · ")}</small>{/if}
          {#if job.lastErrorMessage && ["Failed", "RetryScheduled", "Deferred"].includes(job.state) && job.lastErrorMessage !== job.summary}<small class="job-row-error">{activityText(job.lastErrorMessage)}</small>{/if}
        </span>
        <span class="job-row-end">
          <Badge tone={activityTone(job.displayState || job.state)}>{job.cancellationRequestedAt ? "Cancellation requested" : statusLabel(job)}</Badge>
          <time datetime={finishedAt(job)} title={time(finishedAt(job))}>{runs.length > 1 ? `${clock(finishedAt(runs.at(-1)!))}–${clock(finishedAt(job))}` : clock(finishedAt(job))}</time>
        </span>
        <ChevronDown class="job-row-chevron" size={18} aria-hidden="true" />
      </button>
      {#if job.canCancel && !job.cancellationRequestedAt && queueStates.includes(job.state)}<Button variant="secondary" onclick={() => { cancelJob = job; cancelError = ""; cancelOpen = true; }}>Cancel job</Button>{/if}
    </div>
    {#if percent !== null}<div class="job-row-progress"><Progress value={percent} aria-label={`${percent}% complete`} /><small>{info.completed} of {info.total} · {percent}%</small></div>{/if}
    {#if open}
      <div class="job-row-detail">
        {#if job.recovery}<p><strong>Next step:</strong> {activityText(job.recovery)}</p>{/if}
        <details class="event-technical"><summary>Technical details</summary><dl><div><dt>Job reference</dt><dd>{job.id}</dd></div><div><dt>Operation</dt><dd>{job.type}</dd></div>{#if job.lastErrorCode}<div><dt>Reason code</dt><dd>{job.lastErrorCode}</dd></div>{/if}{#each Object.entries(job.technicalDetails ?? {}) as [name, value]}<div><dt>{name}</dt><dd>{value}</dd></div>{/each}</dl></details>
        {#if runs.length > 1}
          <ul class="job-runs" aria-label="Runs">
            {#each runs as run (run.id)}<li data-job-id={run.id}><time datetime={finishedAt(run)}>{time(finishedAt(run))}</time><span>{jobState(run.state)}</span></li>{/each}
          </ul>
        {/if}
        {@render attemptHistory(job)}
      </div>
    {/if}
  </article>
{/snippet}

<section class="activity-work" aria-label="Jobs" aria-busy={busy}>
  <div class="job-toolbar">
    <SelectField label="Job state" bind:value={stateFilter} options={[{ value: "", label: "All states" }, ...stateOptions]} />
    <SelectField label="Job type" bind:value={typeFilter} options={[{ value: "", label: "All work" }, ...Object.entries(jobNames).map(([value, label]) => ({ value, label }))]} />
    <Button bind:ref={refreshButton} variant="secondary" disabled={busy} onclick={() => void load()}><RefreshCw size={16} aria-hidden="true" />Refresh jobs</Button>
  </div>
  {#if linkedLoading}<p role="status">Opening related work…</p>{/if}
  {#if linkedError}<p class="notice-error" role="alert">{linkedError}</p>{/if}
  {#if linkedJob}
    <section class="event-log-day" aria-label="Selected operation">
      <h2>Selected operation</h2><div class="event-log-list">{@render jobRow([linkedJob])}</div>
    </section>
  {/if}
  {#if loading}
    <Skeleton class="skeleton-panel" aria-label="Loading jobs" aria-busy="true" />
  {:else if error && !queue.length && !finished.length}
    <RouteError eyebrow="Jobs unavailable" title="Background work is unavailable." message={error} onRetry={() => load("replace")} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error} <Button variant="secondary" onclick={() => void load()}>Retry jobs</Button></p>{/if}
    <p class="sr-only" role="status" aria-live="polite">{queue.length} queued and {finished.length} finished jobs loaded.</p>
    {#if queue.length}
      <section class="event-log-day" aria-label="Running and waiting">
        <h2>Running and waiting</h2>
        <div class="event-log-list">{#each queue.filter(item => item.id !== linkedJob?.id) as job (job.id)}{@render jobRow([job])}{/each}</div>
      </section>
    {/if}
    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">{#each day.rows as runs (runs[0].id)}{@render jobRow(runs)}{/each}</div>
      </section>
    {/each}
    {#if !queue.length && !finished.length && !linkedJob && !linkedLoading && !linkedError}
      <div class="compact-empty"><strong>{stateFilter || typeFilter ? "No jobs match these filters" : "No background work yet"}</strong><p>Running, waiting and finished work appears here automatically.</p></div>
    {/if}
    {#if hasMore}<nav class="activity-pagination" aria-label="Job pages"><Button variant="secondary" disabled={busy} onclick={() => void load("older")}>Load earlier jobs</Button></nav>{/if}
  {/if}
</section>
<ConfirmDialog bind:open={cancelOpen} title="Cancel this job?" description="Completed work remains recorded. The worker stops at its next safe cancellation point." confirmLabel="Cancel job" cancelLabel="Keep running" disabled={cancelling} error={cancelError} closeOnConfirm={false} onConfirm={cancel} />

<style>
  .activity-work { display: grid; gap: var(--section-gap); margin-bottom: var(--section-gap); min-width: 0; }
  p { margin: 0; color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .job-toolbar { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)) auto; gap: .75rem; align-items: end; }
  .job-row + .job-row { border-top: 1px solid var(--color-edge); }
  .job-row-line { display: flex; align-items: center; gap: .5rem; padding-right: var(--space-4); }
  .job-row-toggle {
    flex: 1; min-width: 0; display: grid; grid-template-columns: 2.5rem minmax(0, 1fr) auto 1.125rem; align-items: center;
    gap: var(--space-4); min-height: 4rem; padding: var(--space-3) var(--space-5); border: 0; background: transparent;
    color: inherit; font: inherit; text-align: left; cursor: pointer;
  }
  .job-row-line:has(.job-row-toggle:hover), .job-row:has(.job-row-toggle[aria-expanded="true"]) { background: color-mix(in srgb, var(--color-ink) 4%, transparent); }
  .job-row-toggle:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  .job-row-copy { display: grid; min-width: 0; gap: .125rem; }
  .job-row-copy strong { overflow: hidden; color: var(--color-ink); font-size: var(--text-sm); line-height: 1.25rem; font-weight: 500; text-overflow: ellipsis; white-space: nowrap; }
  .job-row-copy small { overflow: hidden; color: var(--color-ink-muted); font-size: .8125rem; line-height: 1.125rem; text-overflow: ellipsis; white-space: nowrap; }
  .job-row-copy .job-row-context { white-space: normal; overflow-wrap: anywhere; }
  .event-technical > summary { min-height: var(--control-md); display: flex; align-items: center; cursor: pointer; }
  .event-technical dl { display: grid; gap: .5rem; margin: 0; overflow-wrap: anywhere; }
  .event-technical dd { margin: 0; font-size: .8125rem; }
  .job-row-copy .job-row-error { color: var(--color-danger); white-space: normal; }
  .job-row-end { display: flex; align-items: center; gap: var(--space-4); }
  .job-row-end time { min-width: 4.5rem; color: var(--color-ink-muted); font-size: .8125rem; text-align: right; white-space: nowrap; }
  :global(.job-row-chevron) { color: var(--color-ink-muted); transition: transform 160ms ease; }
  .job-row-toggle[aria-expanded="true"] :global(.job-row-chevron) { transform: rotate(180deg); }
  .job-row-progress { display: grid; gap: .25rem; padding: 0 var(--space-5) var(--space-3) calc(2.5rem + var(--space-5) + var(--space-4)); }
  .job-row-progress small { color: var(--color-ink-muted); font-size: .8125rem; }
  .job-row-detail { display: grid; gap: .75rem; padding: 0 var(--space-5) var(--space-4) calc(2.5rem + var(--space-5) + var(--space-4)); }
  .job-runs { display: grid; gap: .25rem; margin: 0; padding: 0; list-style: none; }
  .job-runs li { display: flex; justify-content: space-between; gap: 1rem; color: var(--color-ink-muted); font-size: .8125rem; }
  .job-attempts { display: grid; gap: .5rem; padding: .75rem 1rem; border-radius: var(--radius-md); background: var(--md-sys-color-surface-container); }
  .job-attempts article { display: grid; gap: .125rem; }
  .job-attempts article + article { padding-top: .5rem; border-top: 1px solid var(--md-sys-color-outline-variant); }
  .job-attempts strong { font-size: .8125rem; font-weight: 600; color: var(--color-ink); }
  .activity-pagination { display: flex; justify-content: center; }
  @media (prefers-reduced-motion: reduce) { :global(.job-row-chevron) { transition: none; } }
  @media (max-width: 650px) {
    .job-toolbar { grid-template-columns: minmax(0, 1fr); }
    .job-row-line { flex-direction: column; align-items: stretch; gap: 0; padding: 0; }
    .job-row-line > :global([data-slot="button"]) { align-self: flex-start; margin: 0 0 var(--space-3) calc(2.5rem + var(--space-4) * 2); }
    .job-row-toggle { grid-template-columns: 2.5rem minmax(0, 1fr) 1.125rem; gap: var(--space-3); padding-inline: var(--space-4); }
    .job-row-end { grid-column: 2; grid-row: 2; gap: var(--space-3); }
    .job-row-end time { min-width: 0; text-align: left; }
    .job-row-copy small { white-space: normal; overflow-wrap: anywhere; }
    .job-row-progress, .job-row-detail { padding-left: calc(2.5rem + var(--space-4) * 2); padding-right: var(--space-4); }
  }
</style>
