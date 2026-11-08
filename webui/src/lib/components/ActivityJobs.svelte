<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { onMount, tick, untrack } from "svelte";
  import { activityWork, home, type Job, type JobDetail, type JobProgress } from "$lib/api";
  import { dayLabel, jobName, jobNames, jobState, progressPercent, activityText, validJobId, jobResultLabel, jobSummary, clockRange, groupAttempts, activityRecovery, internalActivityLink } from "$lib/activity";
  import { progressDetails } from "$lib/jobs";
  import { liveUpdates, createRefreshScheduler } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import { Progress } from "$lib/components/ui/progress";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import GroupedRow from "$lib/components/patterns/GroupedRow.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ActivityIcon from "$lib/components/ActivityIcon.svelte";
  import ActivityStatusIcon from "$lib/components/ActivityStatusIcon.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
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
  let preferencesReady = $state(false);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let stateFilter = $state("");
  let typeFilter = $state("");
  let cursor = $state("");
  let cursorId = $state("");
  let hasMore = $state(false);
  let expanded = $state("");
  let panelOpen = $state(false);
  let selectedRuns = $state<Job[]>([]);
  let filter = $state("all");

  const visibleQueue = $derived(["all", "queue"].includes(filter) ? queue : []);
  let detail = $state<JobDetail | null>(null);
  const selectedJob = $derived(detail?.job.id === expanded ? detail.job : selectedRuns.find(job => job.id === expanded));
  let detailBusy = $state(false);
  let detailError = $state("");
  let cancelJob = $state<Job | null>(null);
  let cancelOpen = $state(false);
  let cancelling = $state(false);
  let cancelError = $state("");
  let refreshButton = $state<HTMLButtonElement | null>(null);
  let request = 0;
  let opener: HTMLElement | null = null;
  let disposed = false;
  let refreshPending = false;
  const stateOptions = [...queueStates, ...finishedStates].map((value) => ({ value, label: ({ Pending: "Waiting to start", Deferred: "Waiting between batches", RetryScheduled: "Waiting to retry" } as Record<string, string>)[value] || jobState(value) }));
  const wantsQueue = $derived(!stateFilter || queueStates.includes(stateFilter));
  const wantsFinished = $derived(!stateFilter || finishedStates.includes(stateFilter));

  // Routine runs that finished the same way collapse into one row per day.
  const days = $derived.by(() => {
    const result: Array<{ label: string; rows: Job[][] }> = [];
    for (const job of finished.filter(item => item.id !== linkedJob?.id && filter !== "queue" && (filter !== "failed" || item.state === "Failed"))) {
      const label = dayLabel(finishedAt(job));
      let day = result.at(-1);
      if (day?.label !== label) result.push(day = { label, rows: [] });
      const previous = day.rows.at(-1)?.[0];
      if (previous && !job.lastErrorMessage && !previous.lastErrorMessage && previous.type === job.type &&
          previous.state === job.state && previous.ownerName === job.ownerName && previous.source === job.source &&
          previous.displayName === job.displayName && previous.displayState === job.displayState)
        day.rows.at(-1)!.push(job);
      else day.rows.push([job]);
    }
    return result;
  });

  function finishedAt(job: Job) { return job.completedAt ?? job.createdAt ?? job.updatedAt; }
  function time(value?: string | null) {
    return formatLocalDateTime(value, "Not recorded");
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
    return ["Deferred", "RetryScheduled", "Pending"].includes(job.state) ? "Waiting" : jobResultLabel(job);
  }
  function resultState(job: Job) {
    return ["Deferred", "RetryScheduled", "Pending"].includes(job.state) ? "waiting" : statusLabel(job) === "Passing" ? "passed" : job.displayState || job.state;
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
    if (!panelOpen) opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    panelOpen = true;
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
      linkedJob = next.job; detail = next; expanded = next.job.id; selectedRuns = [next.job]; panelOpen = true;
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

  $effect(() => {
    if (!preferencesReady) return;
    try { localStorage.setItem("activity.jobs.options." + scope, JSON.stringify({ state: stateFilter, type: typeFilter })); } catch {}
  });

  onMount(() => {
    try {
      const saved = JSON.parse(localStorage.getItem("activity.jobs.options." + scope) || "{}");
      if ([...queueStates, ...finishedStates].includes(saved.state)) stateFilter = saved.state;
      if (Object.hasOwn(jobNames, saved.type)) typeFilter = saved.type;
    } catch {}
    preferencesReady = true;
    mounted = true;
    if (initialJobId) void openLinkedJob(initialJobId);
    const unsubscribe = liveUpdates.subscribe(() => { scheduler.schedule(); if (expanded && !detailBusy) { const job = [...(linkedJob ? [linkedJob] : []), ...queue, ...finished].find((item) => item.id === expanded); if (job) void attempts(job, true); } });
    return () => { disposed = true; request++; unsubscribe(); scheduler.cancel(); };
  });
</script>

{#snippet attemptTimeline(groups: ReturnType<typeof groupAttempts>)}
  <ol class="attempt-timeline">
    {#each groups as attempts (attempts[0].attemptNumber)}
      {@const latest = attempts[0]}
      {@const outcome = latest.outcome || (latest.completedAt ? "unknown" : "running")}
      <li>
        <ActivityStatusIcon state={outcome} label={jobState(outcome)} />
        <div><strong>{attempts.length > 1 ? countLabel(attempts.length, "attempt") : "Attempt " + latest.attemptNumber} · {jobState(outcome)}</strong>
          <p>{time(attempts.at(-1)!.startedAt)}{attempts.length > 1 ? "–" + clock(latest.completedAt || latest.startedAt) : ""}</p>
          {#if latest.errorMessage}<p>{activityText(latest.errorMessage)}</p>{/if}
        </div>
      </li>
    {/each}
  </ol>
{/snippet}

{#snippet attemptHistory(job: Job)}
  {@const groups = groupAttempts(detail?.attempts ?? [])}
  <section class="job-attempts" id={`job-${job.id}-attempts`} aria-label="Attempt history" aria-busy={detailBusy}>
    <h3>Attempts</h3>
    {#if detailError}<ErrorState title={detailError} onretry={() => attempts(job)} />{/if}
    {#if !detail && detailBusy}<p role="status">Loading attempts…</p>{/if}
    {@render attemptTimeline(groups.slice(0, 4))}
    {#if groups.length > 4}
      <details><summary>{countLabel(groups.slice(4).reduce((count, group) => count + group.length, 0), "earlier attempt")}</summary>{@render attemptTimeline(groups.slice(4))}</details>
    {/if}
    {#if detail && !groups.length}<p>No retained attempts. {countLabel(job.attemptCount ?? 0, "attempt is", "attempts are")} recorded on this job.</p>{/if}
    {#if detail?.hasMore}<Button variant="secondary" disabled={detailBusy} onclick={() => void attempts(job, true)}>Load earlier attempts</Button>{/if}
  </section>
{/snippet}

{#snippet jobRow(runs: Job[])}
  {@const job = runs[0]}
  {@const latest = progress.find(entry => entry.jobId === job.id)}
  {@const info = latest ? progressDetails(latest) : {}}
  {@const percent = progressPercent(info.completed, info.total)}
  {@const waiting = ["RetryScheduled", "Deferred", "Pending"].includes(job.state) && job.availableAt}
  <article class="job-row" data-job-id={job.id} id={"job-" + job.id} tabindex="-1">
    <ItemRow title={jobName(job.type, job.displayName) + (runs.length > 1 ? " · " + runs.length + " runs" : "")} subtitle={[meta(job), info.message, info.playlist, info.track, job.summary ? jobSummary(job.summary) : job.targetName ? activityText(job.targetName) : job.lastErrorMessage ? activityText(job.lastErrorMessage) : "", waiting ? "Next try " + clock(job.availableAt) : ""].filter(Boolean).join(" · ")} selected={panelOpen && selectedRuns[0]?.id === job.id} onclick={() => { selectedRuns = runs; void attempts(job); }}>
      {#snippet leading()}<ActivityStatusIcon state={resultState(job)} kind={kind(job.type)} label={statusLabel(job)} />{/snippet}
      {#snippet pill()}<ActivityOutcome state={resultState(job)} label={job.cancellationRequestedAt ? "Cancellation requested" : statusLabel(job)} />{/snippet}
      {#snippet trailing()}
        <time datetime={finishedAt(job)} title={time(finishedAt(job))}>{clockRange(finishedAt(runs.at(-1)!), finishedAt(job))}</time>
        {#if job.canCancel && !job.cancellationRequestedAt && queueStates.includes(job.state)}<Button variant="text" size="sm" class="cancel-job" onclick={() => { cancelJob = job; cancelError = ""; cancelOpen = true; }}>Cancel</Button>{/if}
      {/snippet}
    </ItemRow>
    {#if percent !== null}<div class="job-row-progress"><Progress value={percent} aria-label={percent + "% complete"} /><small>{info.completed} of {info.total} · {percent}%</small></div>{/if}
  </article>
{/snippet}

<section class="activity-work" aria-label="Jobs" aria-busy={busy}>
  <PageStatus label="Jobs status" items={[
    { label: loading ? "Loading jobs…" : queue.length + " running or waiting" },
    { label: countLabel(finished.length, "finished job") + " loaded" },
    { label: finished.some(job => job.state === "Failed") ? finished.filter(job => job.state === "Failed").length + " failed" : "Nothing failed", tone: finished.some(job => job.state === "Failed") ? "warn" : "neutral" },
  ]} />
  <div class="job-toolbar">
    <FilterSegments label="Job filters" storageKey={"activity.jobs." + scope} bind:value={filter} options={[
      { id: "all", label: "All", count: queue.length + finished.length },
      { id: "queue", label: "Running and waiting", count: queue.length },
      { id: "failed", label: "Failed", count: finished.filter(job => job.state === "Failed").length },
      { id: "finished", label: "Finished", count: finished.length },
    ]} />
    <SelectField label="Job type" bind:value={typeFilter} options={[{ value: "", label: "All work" }, ...Object.entries(jobNames).map(([value, label]) => ({ value, label }))]} />
    <RefreshButton bind:ref={refreshButton} onrefresh={() => load()} label="Refresh jobs" />
  </div>
  <AdvancedFold class="extra-filters" label={`More filters${stateFilter ? " · " + jobState(stateFilter) : ""}`}><SelectField label="Job state" bind:value={stateFilter} options={[{ value: "", label: "All states" }, ...stateOptions]} /></AdvancedFold>
  {#if linkedLoading}<p role="status">Opening related work…</p>{/if}
  {#if linkedError}<ErrorState title={linkedError} onretry={() => openLinkedJob(initialJobId)} />{/if}
  {#if linkedJob}
    <section class="event-log-day" aria-label="Selected operation">
      <h2>Selected operation</h2><div class="event-log-list">{@render jobRow([linkedJob])}</div>
    </section>
  {/if}
  {#if loading}
    <RowsSkeleton label="Loading jobs" />
  {:else if error && !queue.length && !finished.length}
    <ErrorState title={error} label="Jobs unavailable" onretry={() => load("replace")} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error} <Button variant="secondary" onclick={() => void load()}>Retry jobs</Button></p>{/if}
    <p class="sr-only" role="status" aria-live="polite">{queue.length} queued and {countLabel(finished.length, "finished job")} loaded.</p>
    {#if visibleQueue.length}
      <section class="event-log-day" aria-label="Running and waiting">
        <h2>Running and waiting</h2>
        <div class="event-log-list">{#each visibleQueue.filter(item => item.id !== linkedJob?.id) as job (job.id)}{@render jobRow([job])}{/each}</div>
      </section>
    {/if}
    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">{#each day.rows as runs (runs[0].id)}{@render jobRow(runs)}{/each}</div>
      </section>
    {/each}
    {#if !visibleQueue.length && !days.length && !linkedJob && !linkedLoading && !linkedError}
      <EmptyState title={stateFilter || typeFilter || filter !== "all" ? "No jobs match these filters" : "No background work yet"} description="Running, waiting and finished work appears here automatically.">{#snippet icon()}<ActivityIcon kind="job" />{/snippet}</EmptyState>
    {/if}
    {#if hasMore}<nav class="activity-pagination" aria-label="Job pages"><Button variant="secondary" disabled={busy} onclick={() => void load("older")}>Load earlier jobs</Button></nav>{/if}
  {/if}
</section>
{#if selectedJob}
  <SidePanel bind:open={panelOpen} title={jobName(selectedJob.type, selectedJob.displayName) + (selectedRuns.length > 1 ? " · " + selectedRuns.length + " runs" : "")} description={meta(selectedJob)} onclose={() => { expanded = ""; detail = null; void tick().then(() => { if (opener?.isConnected) opener.focus({ preventScroll: true }); }); }}>
    {@const latest = progress.find(entry => entry.jobId === selectedJob.id) ?? detail?.progress?.[0]}
    {@const info = latest ? progressDetails(latest) : {}}
    {@const percent = progressPercent(info.completed, info.total)}
    <div class="job-panel">
      <ActivityOutcome state={resultState(selectedJob)} label={statusLabel(selectedJob)} />
      {#if ["Deferred", "RetryScheduled", "Pending"].includes(selectedJob.state)}<p class="waiting-note"><strong>Waiting until {time(selectedJob.availableAt)}.</strong> {selectedJob.summary ? jobSummary(selectedJob.summary) : "Saved work will continue at the next attempt."}</p>{/if}
      {#if percent !== null}<Progress value={percent} aria-label={percent + "% complete"} /><p>{info.completed} of {info.total} · {percent}%</p>{/if}
      {#if info.message || info.playlist || info.track}<p>{[info.message, info.playlist, info.track].filter(Boolean).join(" · ")}</p>{/if}
      <dl class="job-facts"><div><dt>For</dt><dd>{selectedJob.ownerName || "Household"}</dd></div><div><dt>Started</dt><dd>{time(selectedJob.startedAt)}</dd></div><div><dt>Finished</dt><dd>{selectedJob.completedAt ? time(selectedJob.completedAt) : queueStates.includes(selectedJob.state) ? "Still in progress" : "Not recorded"}</dd></div><div><dt>Attempts</dt><dd>{selectedJob.attemptCount ?? 0}</dd></div></dl>
      {#if selectedJob.targetName}<p>{activityText(selectedJob.targetName)}</p>{/if}
      {#if jobSummary(selectedJob.summary)}<p>{jobSummary(selectedJob.summary)}</p>{/if}
      {#if selectedJob.lastErrorMessage && selectedJob.lastErrorMessage !== selectedJob.summary}<p>{activityText(selectedJob.lastErrorMessage)}</p>{/if}
      {#if selectedJob.recovery}<p><strong>Next step:</strong> {activityRecovery(selectedJob.recovery)}</p>{/if}
      {#if selectedRuns.length > 1}
        <GroupedRow count={selectedRuns.length} summary="Runs" tone="neutral" expanded={true}>
          {#snippet members()}{#each selectedRuns as run}<ItemRow title={run.targetName || run.displayName || jobName(run.type)} subtitle={time(finishedAt(run))} selected={expanded === run.id} onclick={() => void attempts(run)}>{#snippet pill()}<ActivityOutcome state={run.displayState || run.state} label={statusLabel(run)} />{/snippet}</ItemRow>{/each}{/snippet}
        </GroupedRow>
      {/if}
      {@render attemptHistory(selectedJob)}
      <details class="event-technical"><summary>Technical details</summary><dl><div><dt>Job reference</dt><dd>{selectedJob.id}</dd></div><div><dt>Operation</dt><dd>{selectedJob.type}</dd></div>{#if selectedJob.lastErrorCode}<div><dt>Reason code</dt><dd>{selectedJob.lastErrorCode}</dd></div>{/if}{#each Object.entries(selectedJob.technicalDetails ?? {}) as [name, value]}<div><dt>{name}</dt><dd>{value}</dd></div>{/each}</dl></details>
    </div>
    {#snippet footer()}<div class="job-panel-footer">{#if selectedJob.canCancel && !selectedJob.cancellationRequestedAt && queueStates.includes(selectedJob.state)}<Button variant="text" size="sm" class="cancel-job" onclick={() => { cancelJob = selectedJob; cancelError = ""; cancelOpen = true; }}>Cancel job…</Button>{/if}<Button variant="text" href="#/activity?view=history">Open in History</Button>{#if internalActivityLink(selectedJob.actionHref)}<Button href={selectedJob.actionHref!}>{selectedJob.actionLabel || "Open related work"}</Button>{/if}</div>{/snippet}
  </SidePanel>
{/if}

<ConfirmDialog bind:open={cancelOpen} title="Cancel this job?" description="Completed work remains recorded. The worker stops at its next safe cancellation point." confirmLabel="Cancel job" cancelLabel="Keep running" disabled={cancelling} error={cancelError} closeOnConfirm={false} onConfirm={cancel} />

<style>
  .activity-work { container-type: inline-size; display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--section-gap); margin-bottom: var(--section-gap); min-width: 0; }
  p { margin: 0; color: var(--md-sys-color-on-surface-variant); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .job-toolbar { display: flex; flex-wrap: wrap; gap: var(--space-3); align-items: center; }
  .job-row + .job-row { border-top: 1px solid var(--color-edge); }
  .job-row time { color: var(--color-ink-muted); font-size: var(--text-xs); white-space: nowrap; font-variant-numeric: tabular-nums; }
  .job-panel { display: grid; gap: var(--space-4); }
  .job-facts { display: grid; gap: var(--space-3); margin: 0; }
  .job-facts > div { display: grid; grid-template-columns: 90px minmax(0, 1fr); gap: var(--space-3); }
  dt, dd { color: var(--color-ink-muted); margin: 0; font-size: var(--text-xs); overflow-wrap: anywhere; }
  .job-panel-footer { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: var(--space-3); }
  :global(.cancel-job) { color: var(--danger); }
  .waiting-note { border-radius: var(--radius-md); padding: var(--space-3); background: var(--md-sys-color-primary-container); color: var(--md-sys-color-on-primary-container); }
  .event-technical summary { min-height: 44px; display: flex; align-items: center; cursor: pointer; }
  :global(.extra-filters) :global(.field) { max-width: 20rem; margin: var(--space-3) var(--space-4); }
  .job-row-progress { display: grid; gap: var(--space-1); padding: var(--space-2) var(--space-4); }
  .job-row-progress small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .job-attempts { display: grid; gap: var(--space-3); }
  .attempt-timeline { list-style: none; margin: 0; padding: 0; }
  .attempt-timeline li { position: relative; display: flex; align-items: start; gap: var(--space-3); padding-block: var(--space-2); }
  .attempt-timeline li + li { border-top: 1px solid var(--color-edge); }
  .attempt-timeline li > div { min-width: 0; }
  .job-attempts h3 { font-size: var(--text-sm); margin: 0; }
  .job-attempts summary { display: flex; min-height: 44px; align-items: center; color: var(--color-signal); cursor: pointer; }
  .job-attempts strong { font-size: var(--text-sm); }
  .activity-pagination { display: flex; justify-content: center; }
  @container (max-width: 650px) { .job-toolbar { display: flex; flex-wrap: wrap; } .job-row :global(.row-controls time) { display: none; } }
</style>
