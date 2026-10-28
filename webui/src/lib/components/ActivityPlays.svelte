<script lang="ts">
  import { onMount } from "svelte";
  import { home, playTraces, type PlayTrace, type ProviderDefinition } from "$lib/api";
  import { dayLabel } from "$lib/activity";
  import { playDuration, playOutcome, playPhase, playSource, playStart } from "$lib/play-traces";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import RouteError from "$lib/components/RouteError.svelte";

  let { administrator = false, scope = "mine", currentUserId = "" }: { administrator?: boolean; scope?: "mine" | "everyone"; currentUserId?: string } = $props();
  let plays = $state<PlayTrace[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let cursor = $state("");
  let hasMore = $state(false);
  let expanded = $state("");
  let detail = $state<PlayTrace | null>(null);
  let detailBusy = $state(false);
  let detailError = $state("");
  let copying = $state(false);
  let disposed = false;
  let detailRequest = 0;
  let refreshPending = false;
  const days = $derived.by(() => {
    const result: { label: string; plays: PlayTrace[] }[] = [];
    for (const play of plays) {
      const label = dayLabel(play.startedAt);
      let day = result.at(-1);
      if (day?.label !== label) result.push(day = { label, plays: [] });
      day.plays.push(play);
    }
    return result;
  });
  const steps = $derived([...(detail?.timing.phases ?? [])].sort((a, b) => a.startedMs - b.startedMs));
  function time(value: string) {
    return Number.isNaN(Date.parse(value)) ? "Time not recorded" : new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
  async function load(older = false) {
    if (busy) { if (!older) refreshPending = true; return; }
    busy = true; error = "";
    try {
      const visible: PlayTrace[] = [];
      let before = older ? cursor : "", more = false;
      do {
        const page = await playTraces.list(before);
        if (disposed) return;
        visible.push(...page.filter(play => !administrator || scope === "everyone" || play.listenerId === currentUserId));
        const next = page.at(-1)?.id ?? "";
        more = page.length === 50 && next !== before;
        before = next;
      } while (more && visible.length < 50);
      const merged = new Map(plays.map(play => [play.id, play]));
      for (const play of visible) merged.set(play.id, play);
      plays = [...merged.values()].sort((a, b) => b.startedAt.localeCompare(a.startedAt) || b.id.localeCompare(a.id));
      if (older || !cursor) { cursor = before; hasMore = more; }
      if (expanded) await loadDetail(expanded, true);
    } catch { if (!disposed) error = "Plays could not be loaded. Try again."; }
    finally {
      if (!disposed) {
        loading = false; busy = false;
        if (refreshPending) { refreshPending = false; scheduler.schedule(); }
      }
    }
  }
  const scheduler = createRefreshScheduler(() => load());
  async function loadDetail(id: string, passive = false) {
    const request = ++detailRequest;
    if (!passive) { detailBusy = true; detailError = ""; }
    try {
      const next = await playTraces.detail(id);
      if (!disposed && request === detailRequest && expanded === id) { detail = next; detailError = ""; }
    } catch { if (!disposed && request === detailRequest && expanded === id) detailError = "Start steps could not be loaded. Try again."; }
    finally { if (!disposed && request === detailRequest) detailBusy = false; }
  }
  function toggle(play: PlayTrace) {
    detailRequest++;
    if (expanded === play.id) { expanded = ""; return; }
    expanded = play.id; detail = null; detailError = "";
    void loadDetail(play.id);
  }
  async function copy(id: string) {
    if (copying) return;
    copying = true;
    try {
      const text = await playTraces.diagnostics(id);
      try {
        if (!navigator.clipboard?.writeText) throw new Error();
        await navigator.clipboard.writeText(text);
      } catch {
        const focus = document.activeElement;
        const field = document.createElement("textarea");
        field.value = text;
        field.setAttribute("aria-hidden", "true");
        field.style.cssText = "position:fixed;left:-9999px;opacity:0";
        document.body.appendChild(field);
        try {
          field.select();
          if (!document.execCommand("copy")) throw new Error();
        } finally {
          field.remove();
          if (focus instanceof HTMLElement) focus.focus({ preventScroll: true });
        }
      }
      toast.success("Copied — paste it to your admin.");
    } catch { toast.error("Diagnostics could not be copied. Try again."); }
    finally { copying = false; }
  }
  onMount(() => {
    void home.schema().then(next => { if (!disposed) providers = next.providers; }).catch(() => {});
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; detailRequest++; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="activity-plays" aria-label="Plays" aria-busy={busy}>
  <div class="plays-toolbar"><RefreshButton variant="secondary" label="Refresh plays" onrefresh={() => load()} /></div>
  {#if loading}
    <Skeleton class="skeleton-panel" aria-label="Loading plays" />
  {:else if error && !plays.length}
    <RouteError eyebrow="Plays unavailable" title="Play history is unavailable." message={error} onRetry={() => load()} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">
          {#each day.plays as play (play.id)}
            {@const outcome = playOutcome(play.outcome)}
            <article class="play-row">
              <button type="button" class="play-toggle" aria-expanded={expanded === play.id} aria-controls={`play-${play.id}`} onclick={() => toggle(play)}>
                <MediaArtwork url={play.request.presented.artwork === "available" ? null : play.request.presented.artwork} />
                <span class="play-copy"><strong>{play.request.presented.title || "Unknown song"}</strong><small>{play.request.presented.artist || "Unknown artist"}</small><small>{playStart(play.timing.firstByteMs)} · {playSource(play.resolution.provider, play.resolution.cached, providers)}</small></span>
                <span class="play-end"><ActivityOutcome state={outcome.state} label={outcome.label} /><time datetime={play.startedAt}>{time(play.startedAt)}</time></span>
              </button>
              {#if expanded === play.id}
                <div class="play-detail" id={`play-${play.id}`} aria-busy={detailBusy}>
                  {#if detailBusy && !detail}<p role="status">Loading start steps…</p>{/if}
                  {#if detailError}<p class="notice-error" role="alert">{detailError}</p><Button variant="secondary" onclick={() => void loadDetail(play.id)}>Retry start steps</Button>{/if}
                  {#if detail}
                    <ol aria-label="Start steps">
                      {#each steps as step}<li><strong>{playPhase(step.phase)}{step.provider ? ` · ${playSource(step.provider, false, providers)}` : ""}</strong><span>{playDuration(step.elapsedMs)}</span></li>{:else}<li>Start steps were not recorded.</li>{/each}
                    </ol>
                    {#if detail.timing.omittedPhases}<p>Some start steps were not retained.</p>{/if}
                  {/if}
                  <Button variant="secondary" disabled={copying} onclick={() => void copy(play.id)}>{copying ? "Copying…" : "Copy diagnostics"}</Button>
                </div>
              {/if}
            </article>
          {/each}
        </div>
      </section>
    {:else}
      <div class="compact-empty"><p>Play something and it will show up here.</p></div>
    {/each}
    {#if hasMore}<div class="plays-toolbar"><Button variant="secondary" disabled={busy} onclick={() => void load(true)}>Load earlier plays</Button></div>{/if}
  {/if}
</section>

<style>
  .activity-plays { display: grid; gap: var(--section-gap); min-width: 0; }
  .plays-toolbar { display: flex; justify-content: flex-end; }
  .play-row + .play-row { border-top: 1px solid var(--color-edge); }
  .play-toggle { display: grid; width: 100%; min-height: 4rem; grid-template-columns: 2.5rem minmax(0, 1fr) auto; gap: var(--space-4); align-items: center; padding: var(--space-3) var(--space-5); border: 0; background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; }
  .play-toggle:hover, .play-toggle[aria-expanded="true"] { background: color-mix(in srgb, var(--color-ink) 4%, transparent); }
  .play-toggle:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  .play-toggle :global(.media-art) { width: 2.5rem; height: 2.5rem; }
  .play-copy { display: grid; gap: .125rem; min-width: 0; }
  .play-copy strong { color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; }
  small, time, p, li { color: var(--color-ink-muted); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .play-end { display: grid; justify-items: end; gap: .25rem; }
  .play-detail { display: grid; justify-items: start; gap: .75rem; padding: var(--space-3) var(--space-5) var(--space-4) calc(2.5rem + var(--space-5) + var(--space-4)); }
  ol { width: 100%; margin: 0; padding-left: 1.25rem; }
  li + li { margin-top: .5rem; }
  li strong { display: block; color: var(--color-ink); font-weight: 500; }
  p { margin: 0; }
  @media (max-width: 650px) {
    .play-toggle { grid-template-columns: 2.5rem minmax(0, 1fr); gap: var(--space-3); padding-inline: var(--space-4); }
    .play-end { grid-column: 2; justify-items: start; }
    .play-detail { padding-inline: var(--space-4); }
  }
</style>
