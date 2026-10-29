<script lang="ts">
  import { onMount } from "svelte";
  import { audioMuse, mixes, playlistLinks, releaseInbox, type AudioMuseStatus, type MixCard, type PlaylistLink, type PlaylistSourceAccount } from "$lib/api";
  import { greeting, mixOrder } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import MixShelf from "$lib/components/MixShelf.svelte";
  import StartMix from "$lib/components/StartMix.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import IntelligenceHistoryImports from "$lib/components/IntelligenceHistoryImports.svelte";
  let { administrator = false, listenerId = "", listenerName = "", initialImport = false }: { initialImport?: boolean; administrator?: boolean; listenerId?: string; listenerName?: string } = $props();
  let cards = $state<MixCard[]>([]), releases = $state<MixCard[]>([]), accounts = $state<PlaylistSourceAccount[]>([]), links = $state<PlaylistLink[]>([]);
  let status = $state<AudioMuseStatus | null>(null), loading = $state(true), error = $state("");
  let hidden = $state(false), importOpen = $state(false), external = $state<MixCard | null>(null), busy = $state(false), actionError = $state("");
  let pending: Promise<void> | null = null, disposed = false;
  const storageKey = $derived(`allstarr.mix-checklist.${listenerId}`);
  const made = $derived(cards.filter(item => item.section === "madeForYou").sort((a, b) => Number(b.readiness.ready) - Number(a.readiness.ready) || mixOrder.indexOf(a.kind) - mixOrder.indexOf(b.kind)));
  const moods = $derived(cards.filter(item => item.section === "moods").sort((a, b) => mixOrder.indexOf(a.kind) - mixOrder.indexOf(b.kind)));
  const custom = $derived(cards.filter(item => item.section === "custom"));
  const connected = $derived(accounts.some(item => item.scope === "personal" && ["lastfm", "listenbrainz"].includes(item.providerId)));
  const daily = $derived(cards.filter(item => item.kind.startsWith("daily-")));
  const dailyReady = $derived(daily.length > 0 && daily.every(item => item.readiness.ready));
  const remaining = $derived(daily.find(item => !item.readiness.ready)?.readiness.message.match(/about (\d+) more/)?.[1]);
  const soundReady = $derived(status?.reason === null && status.analyzedSongs > 0);
  const checklist = $derived(!hidden && (!made.length || made.some(item => !item.readiness.ready) || !connected || administrator && !soundReady));
  const recipes = [
    { source: "listenbrainz", kind: "weekly-jams", name: "Weekly Jams", label: "ListenBrainz", cadence: "weekly" },
    { source: "listenbrainz", kind: "weekly-exploration", name: "Weekly Exploration", label: "ListenBrainz", cadence: "weekly" },
    { source: "lastfm", kind: "loved", name: "Loved on Last.fm", label: "Last.fm", cadence: "daily" },
    { source: "lastfm", kind: "top", name: "Your top tracks", label: "Last.fm", cadence: "daily" },
    { source: "lastfm", kind: "similar", name: "Similar to your top tracks", label: "Last.fm", cadence: "daily" },
  ];
  const suggestions = $derived(recipes.filter(recipe => accounts.some(account => account.scope === "personal" && account.providerId === recipe.source) && !links.some(link => link.sourceProviderId === recipe.source && link.sourcePlaylistId === (recipe.source === "listenbrainz" ? `createdfor:${recipe.kind}` : recipe.kind === "top" ? "top:1month:50" : recipe.kind))).map(recipe => ({ id: `add:${recipe.source}:${recipe.kind}`, kind: "external", name: recipe.name, description: `From ${recipe.label} · ${recipe.cadence}`, section: "yourServices", sortOrder: 0, sourceLabel: recipe.label, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "Ready" }, inApps: false, showInApps: false, appsAllowed: false, coverArtwork: [] }) satisfies MixCard));
  const services = $derived([...cards.filter(item => item.section === "yourServices").map(card => ({ ...card, description: card.sourceLabel ? `From ${card.sourceLabel} · kept in sync` : "From your connected service" })), ...suggestions]);
  async function load() {
    if (pending) return pending;
    pending = (async () => {
      const results = await Promise.allSettled([mixes.list(), playlistLinks.sources(), playlistLinks.list(), releaseInbox.get(), administrator ? audioMuse.status() : Promise.resolve(null)] as const);
      if (disposed) return;
      const [mixResult, sourceResult, linkResult, releaseResult, soundResult] = results;
      if (mixResult.status === "fulfilled") { cards = mixResult.value; error = ""; } else error = "Couldn't load your mixes. Try again.";
      if (sourceResult.status === "fulfilled") accounts = sourceResult.value.accounts ?? [];
      if (linkResult.status === "fulfilled") links = linkResult.value.playlistLinks ?? [];
      if (soundResult.status === "fulfilled") status = soundResult.value;
      if (releaseResult.status === "fulfilled") releases = releaseResult.value.entries.slice(0, 8).map(entry => ({ id: entry.id, kind: "release", name: entry.title, description: `${entry.artist} · ${entry.type === "album" ? "Album" : entry.type === "single" ? "Single" : "Release"}${/^\d{4}-\d{2}-\d{2}$/.test(entry.releaseDate ?? "") ? ` · ${new Date(`${entry.releaseDate}T12:00:00`).toLocaleDateString(undefined, { weekday: "short" })}` : ""}`, section: "madeForYou", sortOrder: 0, sourceLabel: null, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "Ready" }, showInApps: false, inApps: false, appsAllowed: false, coverArtwork: [] }));
      loading = false;
    })().finally(() => { pending = null; });
    return pending;
  }
  $effect(() => { if (initialImport) importOpen = true; });
  function hide() { hidden = true; try { localStorage.setItem(storageKey, "hidden"); } catch { /* Browser storage is optional. */ } }
  function openMix(card: MixCard) { if (card.id.startsWith("add:")) { actionError = ""; external = card; } else window.location.hash = `#/for-you?mix=${encodeURIComponent(card.id)}`; }
  async function addExternal() {
    if (busy || !external) return;
    const [, source, kind] = external.id.split(":"); busy = true; actionError = "";
    try { await pending; await mixes.external(source, kind); external = null; toast.success("Added to your mixes"); await load(); }
    catch { actionError = "This mix could not be added. Try again later."; }
    finally { busy = false; }
  }
  const scheduler = createRefreshScheduler(async () => { if (!busy) await load(); });
  onMount(() => {
    try { hidden = localStorage.getItem(storageKey) === "hidden"; } catch { /* Browser storage is optional. */ }
    void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>
<div class="for-you">
  <div class="section-heading"><div><h2>{greeting(new Date().getHours(), listenerName)}</h2><p>Mixes refresh every morning.</p></div><RefreshButton variant="secondary" label="Refresh page" onrefresh={load} /></div>
  {#if loading}{#each ["Made for you", "Moods"] as title}<section aria-label={`Loading ${title}`}><h2>{title}</h2><div class="loading-shelf">{#each [1, 2, 3, 4, 5, 6] as i}<Skeleton class="mix-skeleton" />{/each}</div></section>{/each}
  {:else}
    {#if checklist}<section class="settings-card checklist" aria-label="Make your mixes better">
      <header><h2>Make your mixes better</h2><Button variant="ghost" onclick={hide}>Hide</Button></header>
      <div class="check-row"><span aria-label={dailyReady ? "Done" : "To do"}>{dailyReady ? "✓" : "○"}</span><span>{dailyReady ? "Enough listening for daily mixes" : `Listen to ${remaining ? `about ${remaining} more` : "more"} songs — or import your history`}</span>{#if !dailyReady}<Button variant="secondary" onclick={() => importOpen = true}>Import history</Button>{/if}</div>
      <div class="check-row"><span aria-label={connected ? "Done" : "To do"}>{connected ? "✓" : "○"}</span><span>Connect Last.fm or ListenBrainz for more discoveries</span>{#if !connected}<Button variant="secondary" href="#/sources">Connect</Button>{/if}</div>
      {#if administrator}<div class="check-row"><span aria-label={soundReady ? "Done" : "To do"}>{soundReady ? "✓" : "○"}</span><span>Turn on sound analysis for mood mixes</span>{#if !soundReady}<Button variant="secondary" href="#/settings/sound-analysis">Set up</Button>{/if}</div>
      {/if}
    </section>{/if}
    {#if error}<p class="muted" role="alert">{error} <Button variant="secondary" onclick={load}>Try again</Button></p>{/if}
    {#if !error && !made.some(item => item.readiness.ready)}<div class="settings-card empty"><p>Your mixes will appear here. Play some music, or import your history to start right away.</p></div>{/if}
    <MixShelf title="Made for you" cards={made} onopen={openMix} />
    <MixShelf title="Moods" cards={moods} onopen={openMix} />
    <StartMix onsaved={async () => { await pending; await load(); }} />
    {#if custom.length}<MixShelf title="Your mixes" cards={custom} onopen={openMix} />{/if}
    {#if releases.length}<MixShelf title="New releases" cards={releases} albumArt seeAll="#/for-you?section=releases" onopen={card => { window.location.hash = `#/for-you?section=releases&release=${encodeURIComponent(card.id)}`; }} />{/if}
    {#if services.length}<MixShelf title="From your services" cards={services} additions={suggestions.map(item => item.id)} onopen={openMix} />{/if}
  {/if}
</div>
<Dialog.Root bind:open={importOpen}><Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog">
  <header><div><Dialog.Title>Bring in your history</Dialog.Title><Dialog.Description>Start your mixes with the music you've already played.</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close history">×</Dialog.Close></header>
  <IntelligenceHistoryImports onchanged={() => void load()} /><footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close></footer>
</Dialog.Content></Dialog.Portal></Dialog.Root>
<ConfirmDialog bind:open={() => Boolean(external), value => { if (!value) external = null; }} onConfirm={addExternal} title="Keep this in your apps?" description={`It updates when ${external?.sourceLabel ?? "your service"} makes a new one.`} confirmLabel="Keep in apps" confirmVariant="default" closeOnConfirm={false} disabled={busy} error={actionError} />
<style>
  .for-you { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--space-6); min-width: 0; }
  .for-you > :global(section) { min-width: 0; }
  .section-heading, header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  .section-heading p, .muted, .empty { color: var(--color-ink-muted); }
  .checklist, .empty { padding: var(--surface-gutter); }
  .check-row { display: grid; grid-template-columns: 24px minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); min-height: 64px; padding-block: var(--space-2); }
  .check-row + .check-row { border-top: 1px solid var(--color-edge); }
  .loading-shelf { display: grid; grid-auto-flow: column; grid-auto-columns: 160px; overflow: hidden; gap: 12px; margin-top: var(--space-3); }
  .loading-shelf :global(.mix-skeleton) { height: 263px; border-radius: var(--radius-md); }
  @media (min-width: 900px) { .loading-shelf { grid-auto-columns: 184px; } .loading-shelf :global(.mix-skeleton) { height: 287px; } }
  @media (max-width: 600px) { .check-row { grid-template-columns: 24px minmax(0, 1fr); } .check-row :global([data-slot="button"]) { grid-column: 2; justify-self: start; } }
</style>
