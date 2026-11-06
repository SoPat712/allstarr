<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { tick, untrack, type Snippet } from "svelte";
  import { sources, type ProviderCheckHistory, type ProviderHealth } from "$lib/api";
  import { checkGuide, humanize } from "$lib/sources";
  import { Badge } from "$lib/components/ui/badge";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { Button } from "$lib/components/ui/button";
  import { sourceCheckCard } from "$lib/source-check-card";
  import CheckStrip from "$lib/components/CheckStrip.svelte";

  let { providerId, statuses, refreshKey, onRetryWindow, automatic = true, checksSupported = true, afterStrip }: {
    providerId: string;
    automatic?: boolean;
    checksSupported?: boolean;
    statuses: ProviderHealth[];
    refreshKey: string;
    onRetryWindow: () => Promise<void>;
    afterStrip?: Snippet;
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
  const componentId = $props.id();
  const cardId = `${componentId}-check-card`;
  let strip = $state<HTMLDivElement>();
  let card = $state<HTMLDivElement>();
  let activeSlot = $state<number | null>(null);
  let keyboardSlot = 29;
  let focused = false;
  let touched = false;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;
  let placement = $state({ left: 0, top: 0, side: "below", align: "center" });

  function positionCard() {
    if (!strip || !card || activeSlot === null) return;
    const bar = strip.children[activeSlot].getBoundingClientRect();
    const bounds = strip.getBoundingClientRect();
    const panel = strip.closest(".panel-body")?.getBoundingClientRect() ?? bounds;
    const height = card.offsetHeight;
    const width = card.offsetWidth;
    const preferred = bar.left + bar.width / 2 - bounds.left - width / 2;
    const left = Math.max(0, Math.min(preferred, bounds.width - width));
    const bottom = Math.min(panel.bottom, window.innerHeight) - 8;
    const top = Math.max(panel.top, 0) + 8;
    const below = bounds.bottom + 8;
    const above = below + height > bottom;
    placement = { left, top: Math.max(top, Math.min(above ? bounds.top - height - 8 : below, bottom - height)) - bounds.top,
      side: above ? "above" : "below", align: preferred > left ? "end" : preferred < left ? "start" : "center" };
  }

  async function showSlot(index: number) {
    clearTimeout(closeTimer);
    activeSlot = index;
    await tick();
    positionCard();
  }

  function closeCard() {
    clearTimeout(closeTimer);
    activeSlot = null;
  }

  function pointerSlot(event: PointerEvent | MouseEvent) {
    const target = event.target instanceof Element ? event.target.closest<HTMLElement>("[data-check-slot]") : null;
    if (target) return Number(target.dataset.checkSlot);
    const bounds = strip?.getBoundingClientRect();
    return bounds?.width ? Math.max(0, Math.min(29, Math.floor((event.clientX - bounds.left) / bounds.width * 30))) : null;
  }

  function hoverSlot(event: PointerEvent) {
    if (event.pointerType === "touch") return;
    touched = false;
    const index = pointerSlot(event);
    if (index !== null) void showSlot(index);
  }

  function leaveCard() {
    if (!focused && !touched) closeTimer = setTimeout(closeCard, 150);
  }

  function navigateStrip(event: KeyboardEvent) {
    if (event.key === "Escape" && activeSlot !== null) {
      event.preventDefault();
      event.stopPropagation();
      closeCard();
      return;
    }
    const index = activeSlot ?? keyboardSlot;
    const next = event.key === "Home" ? 0 : event.key === "End" ? 29 :
      event.key === "ArrowLeft" || event.key === "ArrowDown" ? Math.max(0, index - 1) :
      event.key === "ArrowRight" || event.key === "ArrowUp" ? Math.min(29, index + 1) : null;
    if (next === null) return;
    event.preventDefault();
    keyboardSlot = next;
    void showSlot(next);
  }

  $effect(() => {
    void providerId;
    untrack(closeCard);
    return () => clearTimeout(closeTimer);
  });

  $effect(() => {
    if (activeSlot === null) return;
    const dismiss = (event: PointerEvent) => {
      if (event.target instanceof Node && !strip?.contains(event.target) && !card?.contains(event.target)) closeCard();
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeCard(); }
    };
    document.addEventListener("pointerdown", dismiss, true);
    document.addEventListener("keydown", escape, true);
    window.addEventListener("resize", positionCard);
    document.addEventListener("scroll", positionCard, true);
    return () => {
      document.removeEventListener("pointerdown", dismiss, true);
      document.removeEventListener("keydown", escape, true);
      window.removeEventListener("resize", positionCard);
      document.removeEventListener("scroll", positionCard, true);
    };
  });

  function stripOutcome(check: ProviderCheckHistory["checks"][number]) {
    return check.outcome === "failed" ? "failed" : check.outcome === "neutral" ? "neutral"
      : check.latencyMs != null && Number.isFinite(check.latencyMs) && check.latencyMs >= 1000 ? "slow" : "ok";
  }

  const emptyLabel = $derived(checksSupported ? "No checks yet" : "This source isn't checked automatically");
  const empty = $derived(history !== null && history.checks.length === 0 && !loading && !error);
  const recentChecks = $derived(history?.checks.slice(0, 30) ?? []);
  const stripSlots = $derived([...Array<null>(30 - recentChecks.length).fill(null), ...recentChecks.toReversed()]);
  const activeCheck = $derived(activeSlot === null ? null : stripSlots[activeSlot]);
  const cardContent = $derived(sourceCheckCard(activeCheck ?? null));
  const stripSummary = $derived(`${countLabel(recentChecks.length, "recorded check")}, oldest to newest; ${recentChecks.filter(check => stripOutcome(check) === "failed").length} failed, ${recentChecks.filter(check => stripOutcome(check) === "slow").length} slow, ${recentChecks.filter(check => stripOutcome(check) === "ok").length} ok, ${recentChecks.filter(check => stripOutcome(check) === "neutral").length} not checked; ${countLabel(30 - recentChecks.length, "empty slot")}. Slow means 1 second or more.`);

  function dateValue(value?: string | null) {
    return value ? Date.parse(value) : Number.NaN;
  }

  function timeLabel(value: string) {
    return formatLocalDateTime(value, "Time unavailable");
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
          <small>{checkGuide(pause.automaticPauseReasonCode)?.label ?? "Recent requests failed"}{#if pause.failureCount && pause.failureCount > 0} · {countLabel(pause.failureCount, "recent failure")}{/if}</small>
          <small>{#if pause.automaticPausedAt}Started {timeLabel(pause.automaticPausedAt)} · {/if}Retry after {timeLabel(pause.automaticRetryAt!)}</small>
        </div>
        <div class="panel-heading-actions"><Badge tone="warning">Paused</Badge>
          <Button variant="secondary" disabled={resuming} onclick={() => void resume(pause)}>{resuming ? "Resuming…" : "Resume now"}</Button></div>
      </div>
    {/each}
    {#if resumeError}<p class="settings-impact" role="alert">Couldn’t resume automatic use. Try again.</p>{/if}
  </section>
{/if}

{#if !empty}
<section class="check-history" aria-label="Last 30 checks">
  <h3>Last 30 checks</h3>
  {#if loading}<RowsSkeleton count={1} />
  {:else if error}<ErrorState title="Checks could not be loaded" onretry={() => load(true)} />
  {:else}
    <div class="check-strip-anchor">
      <!-- One tab stop describes the current check while arrow keys explore the group. -->
      <!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
      <CheckStrip bind:element={strip} tabindex={0} label={stripSummary}
        aria-describedby={activeSlot !== null ? cardId : `${cardId}-help`}
        onfocus={() => { focused = true; void showSlot(keyboardSlot); }}
        onblur={() => { focused = false; closeCard(); }} onkeydown={navigateStrip}
        onpointerover={hoverSlot} onpointerleave={leaveCard}
        onpointerdown={event => { touched = event.pointerType === "touch"; if (touched) event.preventDefault(); }}
        onclick={event => {
          const index = pointerSlot(event);
          if (index !== null) { if (touched && activeSlot === index) closeCard(); else void showSlot(index); }
        }}>
        {#each stripSlots as check, index}
          <span aria-hidden="true" data-check-slot={index} data-active={activeSlot === index} data-outcome={check ? stripOutcome(check) : "empty"}></span>
        {/each}
      </CheckStrip>
      <span id={`${cardId}-help`} class="sr-only">Use arrow keys to explore checks, Home or End to reach either end, and Escape to close check details.</span>
      {#if activeSlot !== null}
        <div bind:this={card} id={cardId} class="check-card" role="tooltip" aria-live="polite"
          data-side={placement.side} data-align={placement.align} style:left={`${placement.left}px`} style:top={`${placement.top}px`}
          onpointerenter={() => clearTimeout(closeTimer)} onpointerleave={leaveCard}>
          {#if cardContent && activeCheck}
            <div class="check-card-heading"><strong>{cardContent.capability}</strong><Badge tone={cardContent.tone}>{cardContent.outcome}</Badge></div>
            <span>{cardContent.account}</span>
            <span>{cardContent.relative}</span>
            <time datetime={activeCheck.testedAt}>{cardContent.absolute}</time>
            {#if cardContent.reason}<span>{cardContent.reason}</span>{/if}
            <span>Latency: {cardContent.latency}</span>
          {:else}<strong>No check</strong>{/if}
        </div>
      {/if}
    </div>
    {#if recentChecks.length}<div class="check-axis"><span>{timeLabel(recentChecks.at(-1)!.testedAt)}</span><span>Latest</span></div>{/if}
  {/if}
</section>
{/if}

{#if empty}
  <p class="check-empty">{emptyLabel}</p>
{/if}
{@render afterStrip?.()}
{#if !empty}
<details class="settings-card check-details" aria-label="Recent diagnostic checks">
  <summary class="settings-row" style="grid-template-columns: minmax(0, 1fr); min-height: var(--control-md); cursor: pointer;">
    <div><strong>Latest checks</strong><small>{#if history}{history.counts.passed} passed · {history.counts.failed} failed · {history.counts.neutral} neutral{:else if loading}Loading recent checks…{:else}Recent checks unavailable{/if}</small></div>
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
      <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);"><Button variant="text" onclick={() => visibleCount = history?.checks.length ?? 30}>Show more checks</Button></div>
    {/if}
  {:else if history}
    <div class="settings-row" style="grid-template-columns: minmax(0, 1fr);"><div><small>No recorded diagnostic checks.</small></div></div>
  {/if}
</details>
{/if}

<style>
  .check-history { display: grid; min-width: 0; gap: var(--space-2); }
  .check-history h3 { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; }
  .check-strip-anchor { position: relative; min-width: 0; }
  .check-card { position: absolute; z-index: 2; display: grid; gap: var(--space-1); width: min(264px, 100%); box-sizing: border-box; padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-popover); color: var(--color-ink-muted); box-shadow: var(--shadow-lg); font-size: var(--text-xs); overflow-wrap: anywhere; }
  .check-card-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); color: var(--color-ink); }
  .settings-card.check-details > summary.settings-row { padding-inline: 0; }
  .check-axis { display: flex; justify-content: space-between; gap: var(--space-2); }
  .check-axis, .check-empty { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
</style>
