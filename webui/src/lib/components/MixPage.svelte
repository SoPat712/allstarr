<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { ApiError, activityWork, mixes, taste, type MixDetail, type MixSong } from "$lib/api";
  import { mixDescription, mixExclusions, mixName, mixNextStep, shortReadiness } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import MixCover from "$lib/components/MixCover.svelte";
  import MixAppState from "$lib/components/MixAppState.svelte";
  import MixSongs from "$lib/components/MixSongs.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  let { id, administrator = false }: { id: string; administrator?: boolean } = $props();
  let detail = $state<MixDetail | null>(null), error = $state(""), busy = $state(false), throttled = $state(false);
  let deleteOpen = $state(false);
  let disposed = false, request = 0, currentId = "", pending: Promise<void> | null = null;
  let refreshJob = $state(""), refreshToast: number | undefined;
  let checkingJob = false, checkAgain = false;
  let throttleTimer: ReturnType<typeof setTimeout> | undefined;
  const nextStep = $derived(mixNextStep(detail?.mix.kind ?? "", detail?.mix.readiness.message ?? "", administrator));
  const close = () => { void goto("#/for-you", { keepFocus: true, noScroll: true }); };
  async function load() {
    const target = id;
    if (pending && currentId === target) return pending;
    const version = ++request; currentId = target;
    pending = (async () => {
      try {
        const next = await mixes.get(target);
        if (!disposed && version === request) {
          if (JSON.stringify(detail) !== JSON.stringify(next)) detail = { ...next, songs: JSON.stringify(detail?.songs) === JSON.stringify(next.songs) ? detail!.songs : next.songs };
          error = "";
        }
      } catch { if (!disposed && version === request) error = "Couldn't load this mix. Try again."; }
      finally { if (version === request) pending = null; }
    })();
    return pending;
  }
  $effect(() => { id; detail = null; deleteOpen = false; throttled = false; clearTimeout(throttleTimer); void load(); });
  async function perform(operation: (target: string) => Promise<unknown>) {
    const target = id;
    if (busy) return;
    busy = true;
    try { await pending; await operation(target); if (!disposed && id === target) { await pending; await load(); } }
    catch { if (!disposed) toast.error("Your mix could not be updated. Try again."); }
    finally { if (!disposed) busy = false; }
  }
  async function refresh() {
    await perform(async target => {
      try {
        refreshToast = toast.progress("Refreshing this mix...");
        refreshJob = (await mixes.refresh(target)).jobId;
        await checkRefresh();
      }
      catch (cause) {
        if (refreshToast !== undefined) toast.dismiss(refreshToast);
        refreshJob = "";
        if (cause instanceof ApiError && cause.status === 429) {
          if (id === target && !disposed) {
            throttled = true;
            throttleTimer = setTimeout(() => { throttled = false; }, 15 * 60_000);
          }
          toast.info("Wait 15 minutes before refreshing this mix again.");
        } else throw cause;
      }
    });
  }
  async function checkRefresh() {
    if (!refreshJob) return;
    if (checkingJob) { checkAgain = true; return; }
    checkingJob = true;
    try {
      const status = await activityWork.detail(refreshJob);
      if (disposed || !refreshJob) return;
      if (["Failed", "Cancelled"].includes(status.job.state)) throw new Error("Refresh failed");
      if (status.job.state === "Succeeded") {
        refreshJob = "";
        await pending; await load();
        if (error) throw new Error("Refresh could not be loaded");
        if (refreshToast !== undefined) toast.update(refreshToast, { message: "Your mix has refreshed.", busy: false, kind: "success" });
      }
    } catch {
      refreshJob = "";
      if (!disposed && refreshToast !== undefined) toast.update(refreshToast, { message: "Your mix could not be refreshed. Try again.", busy: false, kind: "error" });
    } finally { checkingJob = false; if (checkAgain) { checkAgain = false; void checkRefresh(); } }
  }
  async function feedback(row: MixSong, kind: "moreLikeThis" | "notForMe" | "neverArtist", today = false) {
    await perform(async target => {
      const before = await taste.get();
      await mixes.feedback(target, row.song.id, kind, today);
      if (disposed) return;
      if (id === target && detail) detail = { ...detail, songs: detail.songs.filter(item => kind === "neverArtist" ? item.song.artist !== row.song.artist : item.song.id !== row.song.id) };
      const after = await taste.get();
      const affected = kind === "notForMe" ? { kind: "song", name: row.song.canonicalRecordingId ?? row.song.id }
        : kind === "neverArtist" ? { kind: "artist", name: row.song.artist } : null;
      const changed = affected && after.nudges.find(item => item.kind === affected.kind && item.name.toLowerCase() === affected.name.toLowerCase());
      toast.success("Your mixes will follow your choice", changed ? { label: "Undo", run: async () => {
        try {
          const latest = (await taste.get()).nudges.find(item => item.kind === changed.kind && item.name.toLowerCase() === changed.name.toLowerCase());
          if (!latest || latest.mode !== changed.mode || latest.expiresAt !== changed.expiresAt) {
            toast.info("Your choice has changed since then. You can edit it in Your taste."); return;
          }
          await taste.remove(changed.kind, changed.name);
          const old = before.nudges.find(item => item.kind === changed.kind && item.name.toLowerCase() === changed.name.toLowerCase());
          if (old) await taste.nudge(old.kind, old.name, old.mode, Boolean(old.expiresAt), Intl.DateTimeFormat().resolvedOptions().timeZone);
          if (!disposed && id === target) { await pending; await load(); }
        } catch { toast.error("Your choice could not be undone. Try Your taste."); }
      } } : undefined, 6000);
    });
  }
  const scheduler = createRefreshScheduler(async () => { if (!busy) await load(); }, 250, 30_000);
  onMount(() => {
    const unsubscribe = liveUpdates.subscribe(event => {
      if (!event.reason && refreshJob && (!event.resourceId || event.resourceId === refreshJob)) void checkRefresh();
      scheduler.schedule();
    });
    return () => { disposed = true; request++; clearTimeout(throttleTimer); unsubscribe(); scheduler.cancel(); if (refreshToast !== undefined) toast.dismiss(refreshToast); };
  });
</script>
<Dialog.Root open={true} onOpenChange={open => { if (!open) close(); }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog mix-drawer">
    <header><div><Dialog.Title>{mixName(detail?.mix.name ?? "Your mix")}</Dialog.Title><Dialog.Description>{detail?.mix.sourceLabel ? `From ${detail.mix.sourceLabel} · kept in sync` : detail ? detail.mix.readiness.ready ? mixDescription(detail.mix.description, detail.mix.kind) : shortReadiness(detail.mix.readiness.message) : "Songs picked for you"}</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close mix">×</Dialog.Close></header>
    <div class="mix-body">
      {#if error}<p role="alert">{error}</p><Button variant="secondary" onclick={load}>Try again</Button>{/if}
      {#if !detail && !error}<Skeleton class="skeleton-panel" aria-label="Loading mix" />{:else if detail}
        <div class="mix-heading"><div class="mix-cover"><MixCover mix={detail.mix} songs={detail.songs} /></div><p>{detail.mix.updatedAt ? `Updated ${new Date(detail.mix.updatedAt).toLocaleString(undefined, { weekday: "long", hour: "numeric", minute: "2-digit" })} · ` : ""}{countLabel(detail.songs.length, "song")}</p></div>
        <div class="mix-actions">
          <MixAppState added={detail.mix.inApps} allowed={detail.mix.appsAllowed} {busy} onadd={() => void perform(target => mixes.add(target))} />
          {#if detail.mix.kind !== "custom" && detail.mix.kind !== "external"}<span title={throttled ? "Wait 15 minutes before refreshing again" : "Refresh this mix"}><RefreshButton label="Refresh mix" disabled={busy || throttled || Boolean(refreshJob)} onrefresh={refresh} /></span>{/if}
          {#if detail.mix.kind === "custom"}<DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label="Mix actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu mix-menu" align="end"><DropdownMenu.Item class="bits-menu-item" onSelect={() => deleteOpen = true}>Delete mix</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>{/if}
        </div>
        {#if !detail.mix.appsAllowed}<p class="muted" id="mix-apps-hint">Your admin hasn't allowed adding mixes.</p>{/if}
        <details class="recipe settings-card">
          <summary>How this mix is made</summary>
          {#if detail.mix.evidence}
            {@const evidence = detail.mix.evidence}
            <div class="recipe-copy">
              <p>{evidence.recipe.sentence}</p>
              {#if detail.mix.kind !== "external" && detail.mix.kind !== "custom"}
                <p>{countLabel(evidence.livePlays, "play")} in Allstarr · {evidence.importedPlays.toLocaleString()} imported{#if evidence.listeningFrom && evidence.listeningTo}{" · "}{new Date(evidence.listeningFrom).toLocaleDateString()}–{new Date(evidence.listeningTo).toLocaleDateString()}{/if}</p>
              {/if}
              {#if evidence.historicalWindow}<p>Only {countLabel(evidence.primaryPlays, "play")} in Allstarr in the last 30 days, so this also uses {countLabel(evidence.fallbackPlays, "imported play")} from {evidence.historicalWindow}.</p>{/if}
              <p>Looked at {countLabel(evidence.candidates, "song")}, {evidence.qualified.toLocaleString()} fit, {evidence.shown} in this mix.</p>
              {#if detail.mix.kind !== "external"}<p>{evidence.fromMusic} from your music, {evidence.fromServices} from your services, at most {evidence.recipe.artistLimit} per artist.</p>{/if}
              {#if evidence.releaseInboxEntries != null}<p>{countLabel(evidence.releaseInboxEntries, "entry", "entries")} in your release inbox at generation.</p>{/if}
              <p>{evidence.recipe.filters}</p><p>{evidence.recipe.order}</p>
              <p>{evidence.recipe.cadence}. {evidence.recipe.shortfall}</p>
              {#if Object.keys(evidence.excluded).length}<p>Left out: {mixExclusions(evidence.excluded)}.</p>{/if}
              {#if evidence.shortfall && (detail.mix.readiness.ready || evidence.shortfall !== detail.mix.readiness.message)}<p>{evidence.shortfall}</p>{/if}
              {#if evidence.providerFailures}<p>{countLabel(evidence.providerFailures, "service lookup")} failed.</p>{/if}
            </div>
          {:else}<p class="recipe-copy">Refresh this mix to see its recipe details.</p>{/if}
        </details>
        {#if !detail.mix.readiness.ready}<div class="settings-card explanation"><p>{detail.mix.readiness.message}</p><Button variant="secondary" href={nextStep.href}>{nextStep.label}</Button></div>
        {/if}
        {#if detail.songs.length}<MixSongs songs={detail.songs} {busy} onfeedback={detail.mix.kind === "external" ? undefined : feedback} />{/if}

      {/if}
    </div>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>
<ConfirmDialog bind:open={deleteOpen} title="Delete mix?" description="This saved mix will be removed from your mixes and apps." confirmLabel="Delete mix" disabled={busy} closeOnConfirm={false} onConfirm={() => perform(async target => { await mixes.delete(target); if (id === target) close(); })} />
<style>
  :global(.mix-drawer) { left: auto; right: 0; top: 0; transform: none; width: min(560px, 100%); height: 100dvh; max-height: 100dvh; border-radius: 0; display: flex; flex-direction: column; overflow: hidden; }
  .mix-body { padding: var(--surface-gutter); overflow-y: auto; min-height: 0; }
  .mix-heading { display: flex; align-items: center; gap: var(--space-4); margin-bottom: var(--space-4); }
  .mix-cover { width: 160px; flex: 0 0 160px; }
  .mix-heading p, .muted { font-size: var(--text-sm); color: var(--color-ink-muted); }
  .mix-actions { display: flex; align-items: center; gap: var(--space-2); }
  .mix-actions > span { display: flex; align-items: center; flex-shrink: 0; }
  #mix-apps-hint { font-size: var(--text-xs); }
  .explanation { padding: var(--surface-gutter); display: grid; gap: var(--space-3); margin-top: var(--space-4); }
  :global(.mix-menu .bits-menu-item) { min-height: 44px; }
  .recipe { margin-top: var(--space-4); padding-inline: var(--surface-gutter); }
  .recipe summary { min-height: 44px; align-content: center; cursor: pointer; font-size: var(--text-sm); }
  .recipe-copy { font-size: var(--text-sm); color: var(--color-ink-muted); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; padding-bottom: var(--space-3); }
  .recipe-copy p { margin-block: var(--space-2); }
  @media (max-width: 600px) { .mix-cover { width: 120px; flex-basis: 120px; } }
</style>
