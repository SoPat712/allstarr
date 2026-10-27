<script lang="ts">
  import { untrack } from "svelte";
  import { sources, type ProviderCheckHistory, type ProviderHealth } from "$lib/api";
  import { checkGuide, humanize } from "$lib/sources";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";

  let { providerId, statuses, refreshKey, onRetryWindow, automatic = true }: {
    providerId: string;
    automatic?: boolean;
    statuses: ProviderHealth[];
    refreshKey: string;
    onRetryWindow: () => Promise<void>;
  } = $props();

  let history = $state<ProviderCheckHistory | null>(null);
  let loading = $state(true);
  let retrying = $state(false);
  let resuming = $state(false);
  let resumeError = $state(false);
  let error = $state(false);
  let visibleCount = $state(6);
  let anchor = $state<{ server: number; received: number } | null>(null);
  let now = $state(0);
  let request = 0;
  let controller: AbortController | undefined;

  function dateValue(value?: string | null) {
    return value ? Date.parse(value) : Number.NaN;
  }

  function timeLabel(value: string) {
    return Number.isFinite(dateValue(value))
      ? new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
      : "Time unavailable";
  }

  const pauses = $derived(statuses.filter(status => automatic && status.supported && status.enabled &&
    status.configuration !== "needs_configuration" && !status.canAttempt &&
    Number.isFinite(dateValue(status.automaticRetryAt)) && dateValue(status.automaticRetryAt) > now));

  async function resume(status: ProviderHealth) {
    if (resuming) return;
    resuming = true;
    resumeError = false;
    try {
      await sources.resume(providerId, status.capability, status.providerAccountId);
      await onRetryWindow();
      await load();
    } catch { resumeError = true; }
    finally { resuming = false; }
  }

  async function load(manual = false) {
    const current = ++request;
    controller?.abort();
    const abort = new AbortController();
    controller = abort;
    if (manual) retrying = true;
    error = false;
    try {
      const result = await sources.history(providerId, abort.signal);
      if (current !== request) return;
      history = result;
      const server = dateValue(result.capturedAt);
      if (Number.isFinite(server)) {
        anchor = { server, received: performance.now() };
        now = server;
      }
    } catch {
      if (current === request && !abort.signal.aborted) error = true;
    } finally {
      if (current === request) { loading = false; retrying = false; }
    }
  }

  $effect(() => {
    void providerId;
    void refreshKey;
    untrack(() => void load());
    return () => { request++; controller?.abort(); };
  });

  $effect(() => {
    const reference = anchor;
    if (!reference) return;
    const current = Math.max(now, reference.server + Math.max(0, performance.now() - reference.received));
    const deadlines = statuses.filter(status => !status.canAttempt && status.supported && status.enabled)
      .map(status => dateValue(status.automaticRetryAt)).filter(value => value > current);
    if (!deadlines.length) return;
    const timer = setTimeout(() => {
      now = reference.server + Math.max(0, performance.now() - reference.received);
      void onRetryWindow();
    }, Math.min(2_147_483_647, Math.max(1, Math.min(...deadlines) - current + 1)));
    return () => clearTimeout(timer);
  });
</script>

{#if pauses.length}
  <section class="settings-card" aria-label="Automatic routing">
    <header><div><h3>Automatic routing</h3><small>Other eligible sources can be used during these temporary skips. Your source switches still apply.</small></div></header>
    {#each pauses as pause}
      <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);">
        <div style="overflow-wrap: anywhere;">
          <strong>Paused from automatic use{pause.failureCount && pause.failureCount > 1 ? " after repeated failures" : ""}</strong>
          <small>{pause.providerAccountName || "Public access"} · {humanize(pause.capability)}</small>
          <small>{checkGuide(pause.automaticPauseReasonCode)?.label ?? "Recent requests failed"}{#if pause.failureCount && pause.failureCount > 0} · {pause.failureCount} recent failures{/if}</small>
          <small>{#if pause.automaticPausedAt}Started {timeLabel(pause.automaticPausedAt)} · {/if}Retry after {timeLabel(pause.automaticRetryAt!)}</small>
        </div>
        <div class="panel-heading-actions"><Badge tone="warning">Paused</Badge>
          <Button variant="secondary" disabled={resuming} onclick={() => void resume(pause)}>{resuming ? "Resuming…" : "Resume now"}</Button></div>
      </div>
    {/each}
    {#if resumeError}<p class="settings-impact" role="alert">Couldn’t resume automatic use. Try again.</p>{/if}
  </section>
{/if}

{#if history?.checks.length}
  <section class="settings-card" aria-label="Recent source outcomes">
    <header><div><h3>Recent outcomes</h3><small>Latest checks and attempts, newest first. A success clears the automatic pause.</small></div></header>
    <ol aria-label="Recent outcomes" style="display: flex; flex-wrap: wrap; gap: var(--space-2); margin: 0; padding: var(--space-3) var(--surface-gutter); list-style: none;">
      {#each history.checks.slice(0, 20) as check}
        <li title={`${humanize(check.capability)} · ${check.accountLabel} · ${timeLabel(check.testedAt)}`}>
          <Badge tone={check.outcome === "passed" ? "success" : check.outcome === "failed" ? "danger" : "neutral"}>{humanize(check.outcome)}</Badge>
        </li>
      {/each}
    </ol>
  </section>
{/if}

<details class="settings-card" aria-label="Recent diagnostic checks">
  <summary class="settings-row" style="grid-template-columns: minmax(0, 1fr); min-height: var(--control-md); cursor: pointer;">
    <div><strong>Recent diagnostic checks ▾</strong><small>{#if history}{history.counts.passed} passed · {history.counts.failed} failed · {history.counts.neutral} neutral{:else if loading}Loading recent checks…{:else}Recent checks unavailable{/if}</small></div>
  </summary>
  <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);">
    <div><small>The latest recorded checks from the last 30 days. Includes recent attempts from this server session. These samples do not measure every stream.</small></div>
  </div>
  {#if error}
    <div class="settings-row" style="grid-template-columns: minmax(0, 1fr) auto;">
      <div role="status"><small>Couldn’t load recent checks.{#if history} Showing the last loaded results.{/if}</small></div>
      <Button variant="secondary" disabled={retrying} onclick={() => void load(true)}>{retrying ? "Retrying…" : "Retry"}</Button>
    </div>
  {/if}
  {#if history?.checks.length}
    <ul style="margin: 0; padding: 0; list-style: none;">
      {#each history.checks.slice(0, visibleCount) as check}
        <li class="settings-row" style="grid-template-columns: minmax(0, 1fr) auto; border-top: 1px solid var(--color-edge);">
          <div style="overflow-wrap: anywhere;">
            <strong>{humanize(check.capability)} · {check.accountLabel}</strong>
            <small>{humanize(check.accountScope)} · {timeLabel(check.testedAt)}</small>
            {#if check.reasonCode}<small>{checkGuide(check.reasonCode)?.label ?? (check.outcome === "neutral" ? "Not checked" : "Check failed")}</small>{/if}
            {#if check.latencyMs != null && Number.isFinite(check.latencyMs) && check.latencyMs >= 0}<small>{check.latencyMs === 0 ? "Under 1 ms" : `${Math.round(check.latencyMs)} ms`}</small>{/if}
          </div>
          <Badge tone={check.outcome === "passed" ? "success" : check.outcome === "failed" ? "danger" : "neutral"}>{humanize(check.outcome)}</Badge>
        </li>
      {/each}
    </ul>
    {#if history.checks.length > visibleCount}
      <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);"><Button variant="ghost" onclick={() => visibleCount = 24}>Show more checks</Button></div>
    {/if}
  {:else if history}
    <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);"><div><small>No recorded diagnostic checks.</small></div></div>
  {/if}
</details>
