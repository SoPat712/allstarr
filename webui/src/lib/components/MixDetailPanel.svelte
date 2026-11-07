<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { ApiError, activityWork, mixes, taste, type MixDetail, type MixSong } from "$lib/api";
  import { mixDescription, mixExclusions, mixName, mixNextStep, shortReadiness } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { Switch } from "$lib/components/ui/switch";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import MixCover from "$lib/components/MixCover.svelte";
  import MixTrackRows from "$lib/components/MixTrackRows.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  let { id, administrator = false }: { id: string; administrator?: boolean } = $props();
  let detail = $state<MixDetail | null>(null), error = $state(""), busy = $state(false), throttled = $state(false);
  let appsChecked = $state(false);
  $effect(() => { appsChecked = detail?.mix.inApps ?? false; });
  async function showInApps(checked: boolean) {
    const target = id;
    await perform(value => checked ? mixes.add(value) : mixes.remove(value));
    if (!disposed && id === target && detail) appsChecked = detail.mix.inApps;
    if (!disposed && id === target && detail?.mix.inApps === checked) toast.success(checked ? "Mix added to your apps" : "Mix hidden from your apps", { label: "Undo", run: () => showInApps(!checked) });
  }
  let deleteOpen = $state(false);
  let disposed = false, request = 0, currentId = "", pending: Promise<void> | null = null;
  let refreshJob = $state(""), refreshToast: number | undefined;
  let checkingJob = false, checkAgain = false;
  let throttleTimer: ReturnType<typeof setTimeout> | undefined;
  const nextStep = $derived(mixNextStep(detail?.mix.kind ?? "", detail?.mix.readiness.message ?? "", administrator));
  const close = () => { const query = new URLSearchParams(location.hash.split("?")[1] ?? ""); query.delete("mix"); void goto(`#/for-you${query.size ? `?${query}` : ""}`, { keepFocus: true, noScroll: true }); };
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
<SidePanel open={true} title={mixName(detail?.mix.name ?? "Your mix")} description={detail?.mix.sourceLabel ? `From ${detail.mix.sourceLabel} · kept in sync` : detail ? mixDescription(detail.mix.description, detail.mix.kind) : "Songs picked for you"} onclose={close}>
  {#snippet artwork()}{#if detail}<MixCover mix={detail.mix} songs={detail.songs} />{/if}{/snippet}
  {#snippet header()}{#if detail}{detail.mix.updatedAt ? `Refreshed ${formatLocalDateTime(detail.mix.updatedAt)} · ` : ""}{countLabel(detail.songs.length, "song")}{/if}{/snippet}
  {#snippet menu()}{#if detail?.mix.kind === "custom"}<DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label="Mix actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="bits-menu mix-menu" align="end"><DropdownMenu.Item class="bits-menu-item destructive" onSelect={() => deleteOpen = true}>Delete mix</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>{/if}{/snippet}
  {#if error}<ErrorState title="Couldn't load this mix" detail={error} onretry={load} />{/if}
  {#if !detail && !error}<RowsSkeleton />{:else if detail}
    <div class="mix-summary">
      <PageStatus items={detail.mix.evidence ? [{ label: `${detail.mix.evidence.fromMusic} from your library` }, { label: `${detail.mix.evidence.fromServices} from services when made` }] : [{ label: `${countLabel(detail.songs.length, "song")} picked for you` }]} />
      {#if detail.mix.evidence}<p>{detail.mix.evidence.recipe.sentence}</p>{/if}
      <div class="apps-row"><label for="mix-show-apps"><strong>Show in my apps</strong><small id="mix-apps-description">Appears as a playlist in your music apps</small></label><Switch id="mix-show-apps" aria-label="Show in my apps" aria-describedby="mix-apps-description" bind:checked={appsChecked} disabled={busy || !detail.mix.inApps && !detail.mix.appsAllowed} onchange={event => void showInApps(event.currentTarget.checked)} /></div><div class="added-state">{#if detail.mix.inApps}<span>✓ In your apps</span><a href="#/library/playlists">Manage in Playlists</a>{/if}</div>
      {#if !detail.mix.appsAllowed}<p class="muted">Your admin hasn't allowed adding mixes.</p>{/if}
    </div>
        {#if detail.mix.evidence}
          <AdvancedFold class="recipe" label="How this mix is made">
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
          </AdvancedFold>
        {/if}

    {#if !detail.mix.readiness.ready}<div class="explanation"><p>{detail.mix.readiness.message}</p><Button variant="outline" href={nextStep.href}>{nextStep.label}</Button></div>{/if}
    {#if detail.songs.length}<div class="songs-heading"><h3>Songs</h3><span>Picked from</span></div><MixTrackRows songs={detail.songs} {busy} onfeedback={detail.mix.kind === "external" ? undefined : feedback} />{/if}
  {/if}
  {#snippet footer()}{#if detail}<div class="mix-footer">
    {#if detail.mix.kind !== "custom" && detail.mix.kind !== "external"}<RefreshButton label="Refresh mix" disabled={busy || throttled || Boolean(refreshJob)} onrefresh={refresh} />{/if}
    {#if !detail.mix.inApps}<Button disabled={busy || !detail.mix.appsAllowed} onclick={() => void showInApps(true)}>Add to apps</Button>{/if}
  </div>{/if}{/snippet}
</SidePanel>
<ConfirmDialog bind:open={deleteOpen} title="Delete mix?" description="This saved mix will be removed from your mixes and apps." confirmLabel="Delete mix" disabled={busy} closeOnConfirm={false} onConfirm={() => perform(async target => { await mixes.delete(target); if (id === target) close(); })} />
<style>
  .mix-summary { display: grid; gap: var(--space-3); }
  .mix-summary p, .muted, .recipe-copy, .explanation { font-size: var(--text-sm); color: var(--color-ink-muted); }
  .apps-row { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); border-block: 1px solid var(--color-edge); padding-block: var(--space-3); }
  .apps-row label { cursor: pointer; }
  .apps-row strong, .apps-row small { display: block; font-size: var(--text-sm); }
  .apps-row small { color: var(--color-ink-muted); }
  .added-state { display: flex; min-height: 44px; flex-wrap: wrap; align-items: center; gap: var(--space-3); font-size: var(--text-xs); }
  .added-state > span { color: var(--success); }
  .added-state a { display: inline-flex; min-height: 44px; align-items: center; }
  .mix-footer { display: flex; width: 100%; justify-content: space-between; align-items: center; gap: var(--space-3); }
  .songs-heading { display: flex; justify-content: space-between; align-items: center; margin-top: var(--space-5); margin-bottom: var(--space-2); font-size: var(--text-xs); color: var(--color-ink-muted); }
  h3 { font-weight: 600; text-transform: uppercase; letter-spacing: .05em; }
  :global(.recipe) { margin-top: var(--space-3); }
  .recipe-copy { font-variant-numeric: tabular-nums; overflow-wrap: anywhere; padding: 0 var(--space-4) var(--space-3); }
  .recipe-copy p { margin-block: var(--space-2); }
  .explanation { display: grid; justify-items: start; gap: var(--space-3); margin-top: var(--space-3); }
  @media (max-width: 360px) { .mix-footer { gap: var(--space-2); } .mix-footer :global([data-slot="button"]) { padding-inline: var(--space-2); } }
  :global(.mix-menu .bits-menu-item) { min-height: 44px; }
  :global(.mix-menu .destructive) { color: var(--danger); }
</style>
