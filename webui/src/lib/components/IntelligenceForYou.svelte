<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { X } from "@lucide/svelte";
  import { onMount, type Component } from "svelte";
  import { goto } from "$app/navigation";
  import { ApiError, activityWork, audioMuse, historyImports, mixes, playlistLinks, releaseInbox, type AudioMuseStatus, type MixCard, type PlaylistLink, type PlaylistSourceAccount } from "$lib/api";
  import { greeting, mixName, mixOrder } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { importPhase } from "$lib/history-import";
  import { progressDetails } from "$lib/jobs";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import { serviceCards, allServiceCards } from "$lib/for-you";
  import MixShelf from "$lib/components/MixShelf.svelte";
  import IntelligenceHistoryImports from "$lib/components/IntelligenceHistoryImports.svelte";
  let { administrator = false, listenerId = "", listenerName = "", initialImport = false }: { initialImport?: boolean; administrator?: boolean; listenerId?: string; listenerName?: string } = $props();
  let startOpen = $state(false);
  let StartMixDialog = $state<Component<any>>();
  async function startMix() {
    try { StartMixDialog ??= (await import("$lib/components/StartMixDialog.svelte")).default; if (!disposed) startOpen = true; }
    catch { toast.error("The mix form could not be loaded. Try again."); }
  }
  let cards = $state<MixCard[]>([]), releases = $state<MixCard[]>([]), accounts = $state<PlaylistSourceAccount[]>([]), links = $state<PlaylistLink[]>([]);
  let status = $state<AudioMuseStatus | null>(null), loading = $state(true), error = $state("");
  let servicesExpanded = $state(false);
  let hidden = $state(false), importOpen = $state(false), busy = $state("");
  let addedSuggestions = $state<string[]>([]), imported = $state(false);
  let pending: Promise<void> | null = null, disposed = false;
  const matching = new Set<() => void>();
  const storageKey = $derived(`allstarr.mix-checklist.${listenerId}`);
  // Recap can send a song or artist here to start a mix from it.
  const startQuery = typeof location === "undefined" ? "" : new URLSearchParams(location.hash.split("?")[1] ?? "").get("start") ?? "";
  const made = $derived(cards.filter(item => item.section === "madeForYou").sort((a, b) => Number(b.readiness.ready) - Number(a.readiness.ready) || mixOrder.indexOf(a.kind) - mixOrder.indexOf(b.kind)));
  const moods = $derived(cards.filter(item => item.section === "moods").sort((a, b) => mixOrder.indexOf(a.kind) - mixOrder.indexOf(b.kind)));
  const custom = $derived(cards.filter(item => item.section === "custom"));
  const connected = $derived(accounts.some(item => item.scope === "personal" && ["lastfm", "listenbrainz"].includes(item.providerId)));
  const daily = $derived(cards.filter(item => item.kind.startsWith("daily-")));
  const dailyReady = $derived(imported || daily.length > 0 && daily.every(item => item.readiness.ready));
  const remaining = $derived(daily.find(item => !item.readiness.ready)?.readiness.message.match(/about (\d+) more/)?.[1]);
  const soundReady = $derived(status?.reason === null && status.analyzedSongs > 0);
  const checklist = $derived(!hidden && (!dailyReady || !connected || administrator && !soundReady));
  const recipes = [
    { source: "listenbrainz", kind: "weekly-jams", name: "Weekly Jams", label: "ListenBrainz" },
    { source: "listenbrainz", kind: "weekly-exploration", name: "Weekly Exploration", label: "ListenBrainz" },
    { source: "lastfm", kind: "loved", name: "Loved on Last.fm", label: "Last.fm" },
    { source: "lastfm", kind: "top", name: "Your top tracks", label: "Last.fm" },
    { source: "lastfm", kind: "similar", name: "Similar to your top tracks", label: "Last.fm" },
  ];
  const suggestions = $derived(recipes.filter(recipe => !addedSuggestions.includes(`add:${recipe.source}:${recipe.kind}`) && accounts.some(account => account.scope === "personal" && account.providerId === recipe.source) && !links.some(link => link.enabled && link.sourceProviderId === recipe.source && link.sourcePlaylistId === (recipe.source === "listenbrainz" ? `createdfor:${recipe.kind}` : recipe.kind === "top" ? "top:1month:50" : recipe.kind))).map(recipe => ({ id: `add:${recipe.source}:${recipe.kind}`, kind: "external", name: recipe.name, description: `From ${recipe.label} · kept in sync`, section: "yourServices", source: recipe.source, sourcePlaylistId: recipe.source === "listenbrainz" ? `createdfor:${recipe.kind}` : recipe.kind === "top" ? "top:1month:50" : recipe.kind, sortOrder: 0, sourceLabel: recipe.label, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "Ready" }, inApps: false, appsAllowed: cards.some(card => card.appsAllowed), coverArtwork: [] }) satisfies MixCard));
  const services = $derived(serviceCards(cards, suggestions));
  async function load() {
    if (pending) return pending;
    pending = (async () => {
      const results = await Promise.allSettled([mixes.list(), playlistLinks.sources(), playlistLinks.list(), releaseInbox.get(), administrator ? audioMuse.status() : Promise.resolve(null), historyImports.list()] as const);
      if (disposed) return;
      const [mixResult, sourceResult, linkResult, releaseResult, soundResult, importResult] = results;
      if (mixResult.status === "fulfilled") { cards = mixResult.value; error = ""; } else error = "Couldn't load your mixes. Try again.";
      if (sourceResult.status === "fulfilled") accounts = sourceResult.value.accounts ?? [];
      if (linkResult.status === "fulfilled") { links = linkResult.value.playlistLinks ?? []; addedSuggestions = []; }
      if (importResult.status === "fulfilled") imported = importResult.value.some(item => importPhase(item) === "completed");
      if (soundResult.status === "fulfilled") status = soundResult.value;
      if (releaseResult.status === "fulfilled") releases = releaseResult.value.entries.slice(0, 8).map(entry => ({ id: entry.id, kind: "release", name: entry.title, description: `${entry.artist} · ${entry.type === "album" ? "Album" : entry.type === "single" ? "Single" : "Release"}${/^\d{4}-\d{2}-\d{2}$/.test(entry.releaseDate ?? "") ? ` · ${new Date(`${entry.releaseDate}T12:00:00`).toLocaleDateString(undefined, { weekday: "short" })}` : ""}`, section: "madeForYou", sortOrder: 0, sourceLabel: null, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "Ready" }, inApps: false, appsAllowed: false, coverArtwork: entry.artworkUrl ? [entry.artworkUrl] : [] }));
      loading = false;
    })().finally(() => { pending = null; });
    return pending;
  }
  $effect(() => { if (startQuery) void startMix(); });
  $effect(() => { if (initialImport) importOpen = true; });
  function hide() { hidden = true; try { localStorage.setItem(storageKey, "hidden"); } catch { /* Browser storage is optional. */ } }
  function openMix(card: MixCard) { if (card.id.startsWith("add:")) void add(card); else void goto(`#/for-you?mix=${encodeURIComponent(card.id)}`, { keepFocus: true, noScroll: true }); }
  function watchExternal(card: MixCard, jobId: string) {
    const notification = toast.progress(`Matching ${card.name} · 0 of 0 songs`);
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    const stop = () => { clearTimeout(timer); clearTimeout(timeout); controller.abort(); matching.delete(cancel); };
    const cancel = () => { stop(); toast.dismiss(notification); };
    const timeout = setTimeout(() => {
      toast.update(notification, { message: "Matching is taking longer than expected. Check your playlist again later.", busy: false, kind: "info" });
      stop();
    }, 15 * 60 * 1000);
    matching.add(cancel);
    async function poll() {
      try {
        const status = await activityWork.detail(jobId, "", "", controller.signal);
        if (controller.signal.aborted) return;
        if (["Failed", "Cancelled"].includes(status.job.state)) throw new Error(status.job.lastErrorMessage || "Playlist matching could not finish. Try again later.");
        if (status.job.state === "Succeeded") {
          const detail = await playlistLinks.details(card.id, undefined, controller.signal);
          if (controller.signal.aborted) return;
          toast.update(notification, { message: `${card.name} is ready · ${detail.matchedCount} of ${countLabel(detail.trackCount, "song")} matched`, busy: false, kind: "success", action: { label: "Open", run: () => openMix(card) } });
          stop();
          void load();
          return;
        }
        const progress = (status.progress ?? []).map(progressDetails).find(item => item.stage === "playlist.match" && item.total != null);
        if (progress) toast.update(notification, { message: `Matching ${card.name} · ${progress.completed ?? 0} of ${countLabel(progress.total ?? 0, "song")}` });
        timer = setTimeout(() => void poll(), 2000);
      } catch (cause) {
        if (controller.signal.aborted) return;
        toast.update(notification, { message: cause instanceof Error ? cause.message : "Playlist matching could not finish. Try again later.", busy: false, kind: "error" });
        stop();
      }
    }
    void poll();
  }
  async function addAll(shelf: MixCard[]) {
    for (const card of shelf.filter(item => !item.inApps && item.appsAllowed && item.readiness.ready)) {
      if (disposed) return;
      if (!await add(card)) break;
    }
  }
  async function add(selected: MixCard) {
    if (busy || !selected.appsAllowed) return;
    busy = selected.id;
    try {
      if (selected.id.startsWith("add:")) {
        const [, source, kind] = selected.id.split(":");
        const card = await mixes.external(source, kind);
        if (disposed) return;
        addedSuggestions = [...addedSuggestions, selected.id];
        cards = [...cards.filter(item => item.id !== card.id), card];
        if (card.initialJobId) watchExternal(card, card.initialJobId);
      } else {
        await mixes.add(selected.id);
        if (disposed) return;
        cards = cards.map(card => card.id === selected.id ? { ...card, inApps: true } : card);
      }
      await pending;
      if (!disposed) await load();
      return true;
    }
    catch (cause) { if (!disposed) toast.error(cause instanceof ApiError && cause.status === 403 ? "Your admin hasn't allowed adding mixes." : cause instanceof Error ? cause.message : "This mix could not be added. Try again later."); return false; }
    finally { busy = ""; }
  }
  async function remove(selected: MixCard) {
    if (busy) return;
    busy = selected.id;
    try {
      await mixes.remove(selected.id);
      if (disposed) return;
      cards = cards.map(card => card.id === selected.id ? { ...card, inApps: false } : card);
      toast.success(`${mixName(selected.name)} is no longer in your apps`, { label: "Undo", run: () => void add({ ...selected, inApps: false }) });
      await pending;
      if (!disposed) await load();
    }
    catch (cause) { if (!disposed) toast.error(cause instanceof Error ? cause.message : "This mix could not be removed. Try again later."); }
    finally { busy = ""; }
  }
  const scheduler = createRefreshScheduler(async () => { if (!busy) await load(); }, 250, 30_000);
  onMount(() => {
    try { hidden = localStorage.getItem(storageKey) === "hidden"; } catch { /* Browser storage is optional. */ }
    void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); for (const cancel of matching) cancel(); };
  });
</script>
<div class="for-you">
  <div class="section-heading"><h2>{greeting(new Date().getHours(), listenerName)}</h2><div class="page-actions"><Button onclick={() => void startMix()}>Start a mix</Button></div></div>
  <PageStatus items={[{ label: `${countLabel(cards.filter(card => card.readiness.ready).length, "mix", "mixes")} ready` }, { label: "Mixes refresh every morning" }]} />
  {#if loading}{#each ["Made for you", "Moods"] as title}<section aria-label={`Loading ${title}`}><h2>{title}</h2><RowsSkeleton count={3} /></section>{/each}
  {:else}
    {#if checklist}<section class="settings-card checklist" aria-label="Get more from your mixes">
      <header><h2>Get more from your mixes</h2><button type="button" class="icon-button" aria-label="Hide tips" onclick={hide}><X size={18} aria-hidden="true" /></button></header>
      {#if !dailyReady}<div class="check-row"><span>Listen to {remaining ? `about ${countLabel(Number(remaining), "more song")}` : "more songs"} — or import your history</span><Button variant="secondary" size="sm" onclick={() => importOpen = true}>Import history</Button></div>{/if}
      {#if !connected}<div class="check-row"><span>Connect Last.fm or ListenBrainz for more discoveries</span><Button variant="secondary" size="sm" href="#/sources">Connect</Button></div>{/if}
      {#if administrator && !soundReady}<div class="check-row"><span>Turn on sound analysis for mood mixes</span><Button variant="secondary" size="sm" href="#/settings/discovery">Set up</Button></div>{/if}
    </section>{/if}
    {#if error}<ErrorState title="Couldn't load your mixes" detail={error} onretry={load} />{/if}
    {#if !error && !made.some(item => item.readiness.ready)}<EmptyState title="Your mixes will appear here" description="Your mixes will appear here. Play some music, or import your history to start right away." />{/if}
    <MixShelf title="Made for you" cards={made} onaddall={() => void addAll(made)} onopen={openMix} {busy} onadd={card => void add(card)} onremove={card => void remove(card)} />
    <MixShelf title="Moods" cards={moods} onaddall={() => void addAll(moods)} onopen={openMix} {busy} onadd={card => void add(card)} onremove={card => void remove(card)} />
    {#if custom.length}<MixShelf title="Your mixes" cards={custom} onaddall={() => void addAll(custom)} onopen={openMix} {busy} onadd={card => void add(card)} onremove={card => void remove(card)} />{/if}
    {#if services.length}<MixShelf title="From your services" columns={5} cards={servicesExpanded ? allServiceCards(services) : services} expanded={servicesExpanded} onseeall={() => servicesExpanded = !servicesExpanded} onaddall={() => void addAll(services)} additions={suggestions.map(item => item.id)} onopen={openMix} {busy} onadd={card => void add(card)} onremove={card => void remove(card)} />{/if}
    {#if releases.length}<MixShelf title="New releases" columns={6} cards={releases} albumArt seeAll="#/for-you?section=releases" onopen={card => { void goto(`#/for-you?section=releases&release=${encodeURIComponent(card.id)}`, { keepFocus: true, noScroll: true }); }} />{/if}
    {#if cards.length && !cards.some(card => card.appsAllowed)}<p class="muted apps-hint">Your admin hasn't allowed adding mixes.</p>{/if}
  {/if}
</div>
{#if startOpen && StartMixDialog}<StartMixDialog appsAllowed={cards.some(card => card.appsAllowed)} initialQuery={startQuery} onclose={() => startOpen = false} onsaved={async () => { await pending; await load(); }} />{/if}
<Dialog.Root bind:open={importOpen}><Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog">
  <header><div><Dialog.Title>Bring in your history</Dialog.Title><Dialog.Description>Start your mixes with the music you've already played.</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close history">×</Dialog.Close></header>
  <IntelligenceHistoryImports onchanged={async () => { await pending; await load(); }} /><footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close></footer>
</Dialog.Content></Dialog.Portal></Dialog.Root>
<style>
  .for-you { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--section-gap); min-width: 0; }
  .for-you > :global(section) { min-width: 0; }
  .page-actions { display: flex; min-width: 0; max-width: 100%; align-items: center; flex-wrap: wrap; gap: var(--space-2); }
  .section-heading, header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  h2 { font-size: 1.125rem; }
  .muted { color: var(--color-ink-muted); }
  .apps-hint { font-size: var(--text-xs); }
  .checklist { padding: var(--surface-gutter); }
  .checklist header { flex-wrap: nowrap; padding: 0; }
  .checklist h2 { font-size: var(--text-sm); }
  .check-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); min-height: 48px; padding-block: var(--space-2); }
  .check-row :global([data-slot="button"]) { min-height: var(--control-md); }
  .check-row + .check-row { border-top: 1px solid var(--color-edge); }
  @media (max-width: 899px) { .page-actions { width: 100%; } .page-actions :global([data-slot="button"]) { max-width: 100%; height: auto; min-height: var(--control-md); white-space: normal; overflow-wrap: anywhere; } }
  @media (max-width: 600px) { .check-row { grid-template-columns: minmax(0, 1fr); } .check-row :global([data-slot="button"]) { grid-column: 1; justify-self: start; } }
</style>
