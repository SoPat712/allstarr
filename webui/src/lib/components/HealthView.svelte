<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { toast } from "$lib/toasts";
  import { Activity } from "@lucide/svelte";
  import { Switch } from "$lib/components/ui/switch";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import CheckStrip from "$lib/components/CheckStrip.svelte";
  import GroupedRow from "$lib/components/patterns/GroupedRow.svelte";
  let { initialPanel = "" }: { initialPanel?: string } = $props();
  let snapshot = $state<HealthSnapshot | null>(null);
  let panelOpen = $state(false), selectedRun = $state(""), selectedStep = $state(""), filter = $state("all");
  let stripIndex = $state(0), stripFocused = $state(false);
  const stripHelpId = $props.id();
  const stripRuns = $derived(snapshot?.runs.slice(0, 30) ?? []);
  const stripSelection = $derived(stripRuns[Math.min(stripIndex, stripRuns.length - 1)]);
  function stripKey(event: KeyboardEvent) {
    if (event.key === "Enter" || event.key === " ") {
      if (stripSelection) { event.preventDefault(); openDetail(stripSelection); }
      return;
    }
    const next = event.key === "ArrowLeft" ? stripIndex + 1 : event.key === "ArrowRight" ? stripIndex - 1 : event.key === "Home" ? stripRuns.length - 1 : event.key === "End" ? 0 : null;
    if (next === null) return;
    event.preventDefault();
    stripIndex = Math.max(0, Math.min(stripRuns.length - 1, next));
  }
  let form = $state<HTMLFormElement>();
  let saveTimer: ReturnType<typeof setTimeout>;
  const currentRun = $derived(snapshot?.runs.find(run => run.id === selectedRun));
  const selectedRunIndex = $derived(snapshot?.runs.findIndex(run => run.id === selectedRun) ?? -1);
  const currentStep = $derived(currentRun?.steps.find(step => step.name === selectedStep));
  const passing = (run: HealthRun) => !run.steps.some(step => step.status === "failed") && run.steps.some(step => step.status === "passed");
  const filteredRuns = $derived((snapshot?.runs ?? []).filter(run => filter === "all" || (filter === "passing" ? passing(run) : run.steps.some(step => step.status === "failed"))));
  const runGroups = $derived.by(() => {
    const groups = new Map<string, HealthRun[]>();
    for (const run of filteredRuns) {
      const key = JSON.stringify(run.steps.map(step => [step.name, step.status, step.reason]));
      groups.set(key, [...(groups.get(key) ?? []), run]);
    }
    return [...groups.values()];
  });
  function openDetail(run: HealthRun, step?: HealthStep) { selectedRun = run.id; selectedStep = step?.name ?? ""; panelOpen = true; }
  function scheduleSave() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => { if (form?.checkValidity()) void save(); }, 150);
  }
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { health, healthError, healthReason, type HealthRun, type HealthSnapshot, type HealthStep } from "$lib/health";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Button } from "$lib/components/ui/button";

  let loading = $state(true), action = $state(""), error = $state(""), message = $state("");
  let enabled = $state(false), listenerUserId = $state(""), intervalMinutes = $state(30);
  let searchQuery = $state("Get Lucky Daft Punk"), firstByteBudgetSeconds = $state(15), revision = $state(0);
  let username = $state(""), password = $state(""), webhookUrl = $state(""), clearWebhook = $state(false);
  let pollLimitReached = $state(false);
  let timer: ReturnType<typeof setTimeout> | undefined;
  let polls = 0, fetching = false;
  const abort = new AbortController();
  const running = $derived(Boolean(snapshot?.runningJobId));
  const locked = $derived(Boolean(action) || running);
  const serverMode = $derived(snapshot?.configuration.mode === "server-api-key" || snapshot?.protocol === "Jellyfin");
  const configured = $derived(Boolean(snapshot?.configuration.hasConfiguration ?? snapshot?.configuration.hasListenerCredential));
  const serviceReady = $derived(Boolean(snapshot?.configuration.serviceCredentialReady));
  const canRun = $derived(configured && (serverMode ? serviceReady : Boolean(snapshot?.configuration.listenerUserId)));
  const credentialsNeeded = $derived(!serverMode && Boolean(listenerUserId) &&
    (!snapshot?.configuration.hasListenerCredential || listenerUserId !== snapshot.configuration.listenerUserId));
  const listenerOptions = $derived((snapshot?.listeners ?? []).map(listener => ({
    value: listener.id, label: listener.displayName,
  })));
  const latest = $derived(snapshot?.runs[0]);
  const failure = $derived(latest?.steps.find(step => step.status === "failed"));
  const stepLabels: Record<string, string> = {
    backend: "Server connection", metadata: "Song details", artwork: "Artwork",
    search: "Search", playback: "Playback", seek: "Seek", "now-playing": "Now playing", scrobble: "Scrobble",
    heart: "Favorite", unheart: "Remove favorite", "playlist-create": "Create playlist", "playlist-add": "Add playlist track",
    "playlist-remove": "Remove playlist track", "playlist-delete": "Delete playlist", download: "Download", "download-seek": "Seek in download",
    lyrics: "Lyrics", cleanup: "Cleanup",
  };
  const alertLabels = {
    none: "No alert needed", not_configured: "Webhook not configured", pending: "Alert pending",
    delivered: "Alert delivered", failed: "Alert delivery failed",
  };

  function when(value: string | null) {
    if (!value) return "Not scheduled";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Time unavailable" : date.toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }

  function latency(value: number | null) {
    return value == null || !Number.isFinite(value) || value < 0 ? "" : value < 1000 ? `${value}\u00a0ms` : `${Number((value / 1000).toFixed(1))}\u00a0s`;
  }

  function stepLabel(step: HealthStep) { return stepLabels[step.name] ?? "Check"; }

  function outcome(run: HealthRun) {
    if (run.steps.some(step => step.status === "failed")) return { label: "Failed", tone: "stop" as const };
    return passing(run) ? { label: "Passing", tone: "ok" as const } : { label: "Not checked", tone: "neutral" as const };
  }
  const counts = (run: HealthRun) => `${countLabel(run.steps.filter(step => step.status === "passed").length, "check")} passed, ${run.steps.filter(step => step.status === "skipped").length} skipped`;

  function applyConfiguration(value: HealthSnapshot) {
    const config = value.configuration;
    enabled = config.enabled; listenerUserId = config.listenerUserId ?? "";
    intervalMinutes = config.intervalMinutes; searchQuery = config.searchQuery;
    firstByteBudgetSeconds = config.firstByteBudgetSeconds; revision = config.revision;
    username = ""; password = ""; webhookUrl = ""; clearWebhook = false;
  }

  function schedulePoll() {
    clearTimeout(timer);
    if (abort.signal.aborted || !snapshot?.runningJobId) return;
    if (polls >= 60) { pollLimitReached = true; return; }
    timer = setTimeout(() => { polls++; void load(); }, 2000);
  }

  async function load(reset = false) {
    if (fetching || abort.signal.aborted) return;
    fetching = true;
    try {
      const value = await health.get(abort.signal);
      const previous = snapshot?.configuration;
      const pristine = previous && enabled === previous.enabled &&
        listenerUserId === (previous.listenerUserId ?? "") && intervalMinutes === previous.intervalMinutes &&
        searchQuery === previous.searchQuery && firstByteBudgetSeconds === previous.firstByteBudgetSeconds &&
        !username && !password && !webhookUrl && !clearWebhook;
      snapshot = value; error = "";
      if (pristine && !reset) applyConfiguration(value);
      if (reset) { applyConfiguration(value); polls = 0; pollLimitReached = false; }
    } catch (cause) {
      if (!abort.signal.aborted) error = healthError(cause);
    } finally {
      fetching = false; loading = false;
      if (!error) schedulePoll();
    }
  }

  async function save(undo = true) {
    if (!snapshot || locked || !serverMode && !listenerUserId) return;
    if (credentialsNeeded && (!username || !password)) return;
    const previous = snapshot.configuration;
    const reversible = !username && !password && !webhookUrl && !clearWebhook;
    action = "save"; error = ""; message = "";
    try {
      const value = await health.save({
        enabled, intervalMinutes, searchQuery: searchQuery.trim(), firstByteBudgetSeconds,
        expectedRevision: revision, clearWebhook,
        ...(!serverMode ? { listenerUserId, ...(username || password ? { username: username.trim(), password } : {}) } : {}),
        ...(!clearWebhook && webhookUrl.trim() ? { webhookUrl: webhookUrl.trim() } : {}),
      }, abort.signal);
      snapshot = value; applyConfiguration(value); message = "Health settings saved.";
      toast.success(message, undo && reversible ? { label: "Undo", run: async () => {
        if (locked) return;
        enabled = previous.enabled; listenerUserId = previous.listenerUserId ?? ""; intervalMinutes = previous.intervalMinutes;
        searchQuery = previous.searchQuery; firstByteBudgetSeconds = previous.firstByteBudgetSeconds;
        await save(false);
      } } : undefined);
      schedulePoll();
    } catch (cause) { if (!abort.signal.aborted) error = healthError(cause); }
    finally { action = ""; }
  }

  async function run() {
    if (!snapshot || !canRun || locked) return;
    action = "run"; error = ""; message = "";
    try {
      const result = await health.run(abort.signal);
      snapshot = { ...snapshot, runningJobId: result.jobId };
      polls = 0; pollLimitReached = false; schedulePoll();
    } catch (cause) { if (!abort.signal.aborted) error = healthError(cause); }
    finally { action = ""; }
  }

  const scheduler = createRefreshScheduler(() => load());
  onMount(() => {
    void load(true).then(() => {
      if (initialPanel && snapshot?.runs.length) {
        const run = snapshot.runs.find(item => item.id === initialPanel) ?? snapshot.runs[0];
        openDetail(run, run.steps.find(step => step.name === initialPanel));
      }
    });
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { unsubscribe(); scheduler.cancel(); clearTimeout(timer); clearTimeout(saveTimer); abort.abort(); };
  });
</script>

<div class="settings-stack">
  <PageStatus items={latest ? [{ label: outcome(latest).label, tone: outcome(latest).tone === "stop" ? "stop" : outcome(latest).tone === "ok" ? "good" : "neutral" }, { label: counts(latest) }, { label: snapshot?.configuration.enabled ? `Every ${snapshot.configuration.intervalMinutes} min` : "Scheduled checks off" }, ...(snapshot?.configuration.nextRunAt ? [{ label: `Next ${when(snapshot.configuration.nextRunAt)}` }] : [])] : [{ label: "No checks yet" }, { label: "Set up monitoring, then run a check" }]} />
  {#if loading}<RowsSkeleton />
  {:else if !snapshot}<ErrorState title="Health monitoring could not be loaded" detail={error} onretry={() => load(true)} />
  {:else}
    <div class="settings-toolbar"><span class="settings-muted">{serverMode ? serviceReady ? "Server API key ready" : "Server API key needed" : configured ? "Listener configured" : "Choose a dedicated test listener"}</span><div class="health-actions"><Button variant="text" disabled={Boolean(action)} onclick={() => { polls = 0; pollLimitReached = false; void load(); }}>Refresh results</Button><Button variant="outline" disabled={locked || !canRun} onclick={() => void run()}>{action === "run" ? "Queuing…" : "Run now"}</Button></div></div>
    {#if !serviceReady && serverMode}<Button href="#/settings/setup" variant="outline">Open Setup</Button>{/if}
    {#if running}<p role="status">A check is running. {pollLimitReached ? "Refresh results to keep following it." : "Results update automatically."}</p>{/if}
    {#if error}<ErrorState title="Health could not finish that request" detail={error} onretry={() => load(true)} />{/if}
    <section class="settings-card check-strip-card" aria-label="Recent checks"><header><h3>Last 30 checks</h3></header>
      <div class="health-strip"><CheckStrip tabindex={0} onkeydown={stripKey} onfocus={() => stripFocused = true} onblur={() => stripFocused = false} aria-describedby={stripHelpId}>
        {#each Array(Math.max(0, 30 - snapshot.runs.length)).fill(0) as _}<span data-outcome="empty" title="No check recorded"></span>{/each}
        {#each stripRuns.toReversed() as run (run.id)}<button type="button" tabindex="-1" data-active={stripFocused && stripSelection?.id === run.id} data-outcome={outcome(run).tone === "ok" ? "ok" : outcome(run).tone === "stop" ? "failed" : "neutral"} aria-label={`${outcome(run).label}, ${when(run.completedAt ?? run.startedAt)}`} title={`${outcome(run).label} · ${when(run.completedAt ?? run.startedAt)}`} onclick={() => openDetail(run)}></button>{/each}
      </CheckStrip><span id={stripHelpId} class="sr-only" aria-live="polite">Use left and right arrows to explore checks, Home and End to reach either end, and Enter to open details.{#if stripSelection} {outcome(stripSelection).label}, {when(stripSelection.completedAt ?? stripSelection.startedAt)}.{/if}</span></div>
      <div class="strip-axis"><span>{snapshot.runs.length ? when(snapshot.runs[Math.min(29, snapshot.runs.length - 1)].startedAt) : "No checks recorded"}</span><span>Now</span></div>
    </section>
    {#if latest}
      <section class="settings-card" aria-label="Latest health check"><header><div><h3>Latest check</h3><small>{when(latest.completedAt ?? latest.startedAt)} · {latest.protocol}</small></div><StatePill tone={outcome(latest).tone} text={outcome(latest).label} /></header>
        {#if failure}<p class="settings-impact"><strong>{stepLabel(failure)} failed.</strong> {healthReason(failure.reason, latest.mode)} {latency(failure.latencyMilliseconds)}</p>{/if}
        <table class="settings-table" aria-label="Check steps"><thead><tr><th>Check</th><th>Time</th><th>Result</th></tr></thead><tbody>
          {#each latest.steps as step, index (`${step.name}-${index}`)}<tr><td><button type="button" class="row-button" aria-label={`Open ${stepLabel(step)}`} onclick={() => openDetail(latest, step)}>{stepLabel(step)}</button></td><td class="number">{latency(step.latencyMilliseconds) || "—"}</td><td><StatePill tone={step.status === "passed" ? "ok" : step.status === "failed" ? "stop" : "neutral"} text={step.status === "passed" ? "Passed" : step.status === "failed" ? "Failed" : "Skipped"} />{#if step.reason}<small>{healthReason(step.reason, latest.mode)}</small>{/if}</td></tr>{/each}
        </tbody></table><p class="settings-impact">{alertLabels[latest.alertStatus]}</p>
      </section>
    {:else}<EmptyState title="No checks yet" description="Save monitoring settings, then run a check to see each step and its timing.">{#snippet icon()}<Activity size={24} />{/snippet}</EmptyState>{/if}

    <form bind:this={form} class="settings-card" aria-label="Health monitoring" onchange={scheduleSave} onsubmit={event => { event.preventDefault(); scheduleSave(); }}>
      <header><h3>Monitoring</h3></header>
      <div class="settings-row"><div><label for="health-enabled"><strong>Enable scheduled checks</strong></label><small>{serverMode ? "Checks use your saved server API key and library media." : "Checks use the selected listener and source tracks."}</small></div><span class="settings-toggle"><Switch id="health-enabled" bind:checked={enabled} disabled={locked} /></span></div>
      {#if !serverMode}
        <div class="settings-row"><div><strong>Health listener</strong><small>A dedicated listener without administrator permissions.</small></div><SelectField label="Health listener" bind:value={listenerUserId} options={listenerOptions} placeholder="Choose a listener" disabled={locked || !listenerOptions.length} onchange={() => { username = ""; password = ""; scheduleSave(); }} /></div>
        {#if !listenerOptions.length}<p class="settings-impact">No eligible listeners. Sign in with a dedicated non-administrator account first.</p>{/if}
        <label class="settings-row"><div><strong>Listener username</strong><small>{credentialsNeeded ? "Verify the selected listener's sign-in." : "A sign-in is saved. Leave both fields blank to keep it."}</small></div><input aria-label="Listener username" autocomplete="username" bind:value={username} required={credentialsNeeded || Boolean(password)} disabled={locked} /></label>
        <label class="settings-row"><div><strong>Listener password</strong></div><input aria-label="Listener password" type="password" autocomplete="current-password" bind:value={password} required={credentialsNeeded || Boolean(username)} disabled={locked} /></label>
      {/if}
      <label class="settings-row"><div><strong>Check interval (minutes)</strong><small>Between 1 and 1,440 minutes.</small></div><input aria-label="Check interval (minutes)" type="number" min="1" max="1440" required bind:value={intervalMinutes} disabled={locked} /></label>
      <details class="settings-fold"><summary>Advanced: sample song, timing and webhook</summary>
        <label class="settings-row"><div><strong>Search query</strong><small>{serverMode ? "A library song to look for. Another sample can be used if unavailable." : "The source track to find for each check."}</small></div><input aria-label="Search query" maxlength="200" required bind:value={searchQuery} disabled={locked} /></label>
        <label class="settings-row"><div><strong>First audio byte budget (seconds)</strong><small>Between 1 and 60 seconds.</small></div><input aria-label="First audio byte budget (seconds)" type="number" min="1" max="60" required bind:value={firstByteBudgetSeconds} disabled={locked} /></label>
        <label class="settings-row"><div><strong>Webhook URL</strong><small>{snapshot.configuration.hasWebhook ? "A webhook is saved. Leave blank to keep it." : "Optional. Receive failure and recovery alerts."}</small></div><input aria-label="Webhook URL" type="password" autocomplete="new-password" bind:value={webhookUrl} disabled={locked || clearWebhook} /></label>
        {#if snapshot.configuration.hasWebhook}<label class="settings-row"><div><strong>Remove saved webhook</strong></div><span class="settings-toggle"><Switch aria-label="Remove saved webhook" bind:checked={clearWebhook} disabled={locked} /></span></label>{/if}
      </details>
      <p class="settings-impact">{serverMode ? "Read-only checks leave listening history and saved music unchanged. App sign-in, favorites and playlist actions are checked separately. Source diagnostics are in Sources." : "Saving permanently marks this listener as a test listener. Its playback is excluded from listening history, recommendations and external scrobbles, even after monitoring is disabled."}</p>
      {#if message}<p class="settings-impact" role="status">{message}</p>{/if}
    </form>
    {#if snapshot.runs.length > 1}
      <FilterSegments value={filter} storageKey="allstarr.settings.health.filter" label="Filter checks" onchange={value => filter = value} options={[{ id: "all", label: "All", count: snapshot.runs.length }, { id: "passing", label: "Passing", count: snapshot.runs.filter(passing).length }, { id: "failed", label: "Failed", count: snapshot.runs.filter(run => run.steps.some(step => step.status === "failed")).length }]} />
      <section class="settings-stack" aria-label="Check history">{#each runGroups as group (group[0].id)}<GroupedRow count={group.length} summary={`${outcome(group[0]).label} · ${counts(group[0])}`} expanded={group.length === 1}>{#snippet members()}{#each group as run (run.id)}<ItemRow title={when(run.completedAt ?? run.startedAt)} subtitle={run.mode === "server-api-key" ? "Server check" : "Listener check"} onclick={() => openDetail(run)} />{/each}{/snippet}</GroupedRow>{:else}<EmptyState title="No checks match this filter" />{/each}</section>
    {/if}
  {/if}
</div>

{#if currentRun}
  {#await import("./patterns/SidePanel.svelte") then { default: SidePanel }}
    <SidePanel bind:open={panelOpen} title={currentStep ? stepLabel(currentStep) : `${currentRun.protocol} check`} description={when(currentRun.completedAt ?? currentRun.startedAt)} navigationLabel="check" onPrevious={snapshot && selectedRunIndex > 0 ? () => openDetail(snapshot!.runs[selectedRunIndex - 1]) : undefined} onNext={snapshot && selectedRunIndex >= 0 && selectedRunIndex < snapshot.runs.length - 1 ? () => openDetail(snapshot!.runs[selectedRunIndex + 1]) : undefined}>
      <div class="settings-stack"><PageStatus items={[{ label: outcome(currentRun).label }, { label: counts(currentRun) }]} />
        {#if currentStep}<section class="settings-card"><div class="settings-row"><div><strong>{stepLabel(currentStep)}</strong><small>{healthReason(currentStep.reason, currentRun.mode) || (currentStep.status === "passed" ? "This check passed." : "This check did not complete.")}</small></div><StatePill tone={currentStep.status === "passed" ? "ok" : currentStep.status === "failed" ? "stop" : "neutral"} text={currentStep.status === "passed" ? "Passed" : currentStep.status === "failed" ? "Failed" : "Skipped"} /></div><p class="settings-impact">{latency(currentStep.latencyMilliseconds) || "No timing recorded"}</p></section>{/if}
        <section class="settings-card"><header><h3>Check steps</h3></header>{#each currentRun.steps as step}<ItemRow title={stepLabel(step)} subtitle={healthReason(step.reason, currentRun.mode) || latency(step.latencyMilliseconds)} onclick={() => selectedStep = step.name}>{#snippet pill()}<StatePill tone={step.status === "passed" ? "ok" : step.status === "failed" ? "stop" : "neutral"} text={step.status === "skipped" ? "Skipped" : step.status === "passed" ? "Passed" : "Failed"} />{/snippet}</ItemRow>{/each}</section>
        <p class="settings-muted">{alertLabels[currentRun.alertStatus]}</p>
      </div>
    </SidePanel>
  {/await}
{/if}
<style>
  .health-strip { padding: var(--space-1) var(--space-4); }
  .health-actions { display: flex; align-items: center; gap: var(--space-2); }
  .strip-axis { display: flex; justify-content: space-between; gap: var(--space-2); padding: var(--space-2) var(--space-4) var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .settings-table td:last-child { width: 45%; }
</style>
