<script lang="ts">
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { health, healthError, healthReason, type HealthRun, type HealthSnapshot, type HealthStep } from "$lib/health";
  import SelectField from "$lib/components/SelectField.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Skeleton } from "$lib/components/ui/skeleton";

  let snapshot = $state<HealthSnapshot | null>(null);
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
    return Number.isNaN(date.getTime()) ? "Time unavailable" : date.toLocaleString();
  }

  function latency(value: number | null) {
    return value == null || !Number.isFinite(value) || value < 0 ? "" : value < 1000 ? `${value}\u00a0ms` : `${Number((value / 1000).toFixed(1))}\u00a0s`;
  }

  function stepLabel(step: HealthStep) { return stepLabels[step.name] ?? "Check"; }

  function outcome(run: HealthRun) {
    if (run.steps.some(step => step.status === "failed")) return { label: "Failed", tone: "danger" as const };
    if (run.mode === "server-api-key" && run.steps.some(step => step.status === "skipped"))
      return { label: run.steps.some(step => step.status === "passed") ? "Partly checked" : "Not checked", tone: "neutral" as const };
    return run.passed ? { label: "Passed", tone: "success" as const } : { label: "Not checked", tone: "neutral" as const };
  }

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

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!snapshot || locked || !serverMode && !listenerUserId) return;
    action = "save"; error = ""; message = "";
    try {
      const value = await health.save({
        enabled, intervalMinutes, searchQuery: searchQuery.trim(), firstByteBudgetSeconds,
        expectedRevision: revision, clearWebhook,
        ...(!serverMode ? { listenerUserId, ...(username || password ? { username: username.trim(), password } : {}) } : {}),
        ...(!clearWebhook && webhookUrl.trim() ? { webhookUrl: webhookUrl.trim() } : {}),
      }, abort.signal);
      snapshot = value; applyConfiguration(value); message = "Health settings saved.";
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
    void load(true);
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { unsubscribe(); scheduler.cancel(); clearTimeout(timer); abort.abort(); };
  });
</script>

{#if loading}
  <Skeleton class="settings-card skeleton-panel" aria-label="Loading Health" aria-busy="true" />
{:else if !snapshot}
  <RouteError eyebrow="Health unavailable" title="Health monitoring could not be loaded." message={error} onRetry={() => load(true)} />
{:else}
  <article class="settings-card" aria-label="Health checks">
    <header>
      <div><h3>{snapshot.protocol} checks</h3><small>{serverMode ? "Read-only server and library checks" : "Provider search, playback and listener actions"}</small></div>
      <Badge tone={running ? "accent" : snapshot.configuration.enabled ? "success" : "neutral"}>{running ? "Running" : snapshot.configuration.enabled ? "Scheduled" : "Off"}</Badge>
    </header>
    <div class="settings-row">
      <div>
        {#if serverMode}
          <strong>{serviceReady ? "Server API key ready" : "Server API key needed"}</strong>
          <small>{serviceReady ? "Uses the server connection saved in Setup. No test listener is needed." : "Set up a Jellyfin server API key before enabling checks."}</small>
          {#if serviceReady && !configured}<small>Save monitoring settings, then run a check.</small>{/if}
          {#if !serviceReady}<Button href="/#/settings/setup" variant="secondary">Open Setup</Button>{/if}
        {:else}
          <strong>{configured ? "Listener configured" : "Choose a dedicated test listener"}</strong>
          <small>{configured ? "Run a check now, or enable a schedule below." : "Save a verified listener sign-in before running checks."}</small>
        {/if}
        <small>{snapshot.inactiveProtocol} is not exposed by this deployment.</small>
      </div>
      <Button disabled={locked || !canRun} onclick={() => void run()}>{action === "run" ? "Queuing…" : "Run now"}</Button>
    </div>
    {#if running}<p class="settings-impact" role="status">A check is running. {pollLimitReached ? "Refresh results to keep following it." : "Results update automatically."}</p>{/if}
    {#if snapshot.configuration.enabled && snapshot.configuration.nextRunAt}<p class="settings-impact">Next check: {when(snapshot.configuration.nextRunAt)}</p>{/if}
    {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
    <footer>
      {#if error}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void load(true)}>Reload health settings</Button>{/if}
      <RefreshButton variant="secondary" disabled={Boolean(action)} onrefresh={() => { polls = 0; pollLimitReached = false; return load(); }} label="Refresh results" />
    </footer>
  </article>

  {#if latest}
    <article class="settings-card" aria-label="Latest health check">
      <header>
        <div><h3>Latest check</h3><small>{latest.protocol} · {latest.mode === "server-api-key" ? "Server API-key check" : "Listener check"} · {when(latest.completedAt ?? latest.startedAt)}</small></div>
        <Badge tone={outcome(latest).tone}>{outcome(latest).label}</Badge>
      </header>
      {#if failure}<p class="settings-impact"><strong>{stepLabel(failure)} failed.</strong> {healthReason(failure.reason, latest.mode)} {latency(failure.latencyMilliseconds)}</p>{/if}
      <ul class="backup-list" aria-label="Check steps">
        {#each latest.steps as step, index (`${step.name}-${index}`)}
          <li>
            <span><strong>{stepLabel(step)}</strong>{#if step.status !== "skipped"}<small>{latency(step.latencyMilliseconds)}</small>{/if}{#if step.reason && step !== failure}<small>{healthReason(step.reason, latest.mode)}</small>{/if}</span>
            <Badge tone={step.status === "passed" ? "success" : step.status === "failed" ? "danger" : "neutral"}>{step.status === "passed" ? "Passed" : step.status === "failed" ? "Failed" : latest.mode === "server-api-key" ? "Not checked" : "Skipped"}</Badge>
          </li>
        {/each}
      </ul>
      <p class="settings-impact">{alertLabels[latest.alertStatus]}</p>
    </article>
  {:else}
    <div class="compact-empty"><strong>No checks yet</strong><p>{configured ? "Run a check to see each step and its timing." : "Save monitoring settings, then run a check to see each step and its timing."}</p></div>
  {/if}

  <form class="settings-card" aria-label="Health monitoring" onsubmit={(event) => void save(event)}>
    <header><h3>Monitoring</h3></header>
    <div class="settings-row">
      <div><label for="health-enabled"><strong>Enable scheduled checks</strong></label><small>{serverMode ? "Checks use your saved server API key and library media." : "Checks use the selected listener and provider tracks."}</small></div>
      <span class="settings-toggle"><Checkbox id="health-enabled" bind:checked={enabled} disabled={locked} /></span>
    </div>
    {#if !serverMode}<div class="settings-row">
      <div><strong>Health listener</strong><small>A dedicated, active listener without administrator permissions.</small></div>
      <SelectField label="Health listener" bind:value={listenerUserId} options={listenerOptions} placeholder="Choose a listener" disabled={locked || !listenerOptions.length} onchange={() => { username = ""; password = ""; }} />
    </div>
    {#if !listenerOptions.length}<p class="settings-impact">No eligible listeners. Sign in with a dedicated non-administrator account first.</p>{/if}
    <div class="settings-row">
      <div><label for="health-username"><strong>Listener username</strong></label><small>{credentialsNeeded || !snapshot.configuration.hasListenerCredential ? "Sign in as the exact selected listener to verify access." : "A sign-in is saved. Leave both fields blank to keep it for this listener."}</small></div>
      <input id="health-username" autocomplete="username" bind:value={username} required={credentialsNeeded || Boolean(password)} disabled={locked} />
    </div>
    <div class="settings-row">
      <div><label for="health-password"><strong>Listener password</strong></label><small>Used to verify and save encrypted listener access.</small></div>
      <input id="health-password" type="password" autocomplete="current-password" bind:value={password} required={credentialsNeeded || Boolean(username)} disabled={locked} />
    </div>{/if}
    <div class="settings-row">
      <div><label for="health-interval"><strong>Check interval (minutes)</strong></label><small>Between 1 and 1,440 minutes.</small></div>
      <input id="health-interval" type="number" min="1" max="1440" required bind:value={intervalMinutes} disabled={locked} />
    </div>
    <div class="settings-row">
      <div><label for="health-query"><strong>Search query</strong></label><small>{serverMode ? "A library song to look for. Another library sample can be used if unavailable." : "The provider track to find for each check."}</small></div>
      <input id="health-query" maxlength="200" required bind:value={searchQuery} disabled={locked} />
    </div>
    <div class="settings-row">
      <div><label for="health-budget"><strong>First audio byte budget (seconds)</strong></label><small>Between 1 and 60 seconds.</small></div>
      <input id="health-budget" type="number" min="1" max="60" required bind:value={firstByteBudgetSeconds} disabled={locked} />
    </div>
    <div class="settings-row">
      <div><label for="health-webhook"><strong>Webhook URL</strong></label><small>{snapshot.configuration.hasWebhook ? "A webhook is saved. Leave blank to keep it." : "Optional. Receive failure and recovery alerts."}</small></div>
      <input id="health-webhook" type="password" autocomplete="new-password" bind:value={webhookUrl} disabled={locked || clearWebhook} />
    </div>
    {#if snapshot.configuration.hasWebhook}
      <div class="settings-row">
        <div><label for="health-clear-webhook"><strong>Remove saved webhook</strong></label><small>Save to stop sending webhook alerts.</small></div>
        <span class="settings-toggle"><Checkbox id="health-clear-webhook" bind:checked={clearWebhook} disabled={locked} /></span>
      </div>
    {/if}
    {#if serverMode}
      <p class="settings-impact">Read-only checks leave listening history and saved music unchanged. App sign-in, favorites and playlist actions remain manual checks. Source diagnostics are in Integrations → Services.</p>
    {:else}
      <p class="settings-impact">Saving permanently marks this listener as synthetic. Its provider playback is excluded from listening history, recommendations and external scrobbles, even after monitoring is disabled or another listener is selected.</p>
    {/if}
    {#if message}<p class="settings-impact" role="status">{message}</p>{/if}
    <footer><Button type="submit" disabled={locked || !serverMode && !listenerUserId}>{action === "save" ? "Saving…" : "Save health settings"}</Button></footer>
  </form>

  {#if snapshot.runs.length > 1}
    <section class="settings-card" aria-label="Check history">
      <header><h3>Recent checks</h3></header>
      <ul class="backup-list">
        {#each snapshot.runs.slice(1) as run (run.id)}
          {@const failed = run.steps.find(step => step.status === "failed")}
          <li>
            <span>
              <strong>{when(run.completedAt ?? run.startedAt)}</strong><small>{run.protocol} · {run.mode === "server-api-key" ? "Server API-key check" : "Listener check"} · {alertLabels[run.alertStatus]}</small>
              {#if failed}<small>{stepLabel(failed)}: {healthReason(failed.reason, run.mode)} {latency(failed.latencyMilliseconds)}</small>{/if}
            </span>
            <Badge tone={outcome(run).tone}>{outcome(run).label}</Badge>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
{/if}
