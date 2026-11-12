<script lang="ts">
  import { onMount, untrack } from "svelte";
  import { home, type ProviderDefinition } from "$lib/api";
  import { artistLinks, artistPill, artistSummary, type ArtistLinkFilter, type ArtistLinkPage, type ArtistLinkRow, type ArtistLinkScope, type ArtistLinkService } from "$lib/artist-links";
  import { countLabel } from "$lib/count-label";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition } from "$lib/sources";
  import { toast } from "$lib/toasts";
  import MatchKindSwitch from "./MatchKindSwitch.svelte";
  import ArtistLinkPanel from "./ArtistLinkPanel.svelte";
  import MediaArtwork from "./MediaArtwork.svelte";
  import ProviderMark from "./ProviderMark.svelte";
  import SearchField from "./SearchField.svelte";
  import PageStatus from "./patterns/PageStatus.svelte";
  import FilterSegments from "./patterns/FilterSegments.svelte";
  import ItemRow from "./patterns/ItemRow.svelte";
  import StatePill from "./patterns/StatePill.svelte";
  import RowsSkeleton from "./patterns/RowsSkeleton.svelte";
  import EmptyState from "./patterns/EmptyState.svelte";
  import ErrorState from "./patterns/ErrorState.svelte";
  import { Button } from "./ui/button";

  let { initialSearch = "", initialArtist = "" }: { initialSearch?: string; initialArtist?: string } = $props();
  let data = $state<ArtistLinkPage | null>(null), loading = $state(true), error = $state("");
  let filter = $state<string>("needs-look"), searchInput = $state(untrack(() => initialSearch)), search = $state(untrack(() => initialSearch));
  let providers = $state<ProviderDefinition[]>([]);
  let open = $state(false), selected = $state<ArtistLinkRow | null>(null), panelLoading = $state(false), panelError = $state("");
  let busy = $state(""), checking = $state(false), loadingMore = $state(false);
  let listRevision = 0, panelRevision = 0, panelController: AbortController | undefined;
  const rows = $derived(data?.artists ?? []);
  const selectedIndex = $derived(rows.findIndex(row => row.id === selected?.id));
  const counts = $derived(data?.counts);

  async function refresh() {
    const revision = ++listRevision;
    try {
      // A refresh keeps the rows already shown, so Load more isn't undone by a live change.
      const page = await artistLinks.list(filter as ArtistLinkFilter, search, null, Math.min(100, Math.max(50, rows.length)));
      if (revision !== listRevision) return;
      data = page; error = "";
    } catch (cause) { if (revision === listRevision) error = cause instanceof Error ? cause.message : "Artists could not load."; }
    finally { if (revision === listRevision) loading = false; }
  }
  async function more() {
    if (!data?.nextCursor || loadingMore) return;
    const revision = listRevision;
    loadingMore = true;
    try {
      const page = await artistLinks.list(filter as ArtistLinkFilter, search, data.nextCursor);
      if (revision !== listRevision || !data) return;
      data = { ...page, artists: [...data.artists, ...page.artists.filter(row => !data!.artists.some(old => old.id === row.id))] };
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "More artists could not load."); }
    finally { loadingMore = false; }
  }
  async function openArtist(id: string, initial?: ArtistLinkRow) {
    panelController?.abort();
    const controller = new AbortController(), revision = ++panelRevision;
    panelController = controller;
    selected = initial ?? null; open = true; panelError = ""; panelLoading = true;
    try { const detail = await artistLinks.get(id, controller.signal); if (revision === panelRevision) selected = detail; }
    catch (cause) { if (!controller.signal.aborted && revision === panelRevision) panelError = cause instanceof Error ? cause.message : "This artist could not load."; }
    finally { if (revision === panelRevision) panelLoading = false; }
  }
  async function change(service: ArtistLinkService, work: () => Promise<ArtistLinkRow>, message: string) {
    if (!selected || busy) return;
    busy = `${service.providerId}:${service.artistId}`;
    try {
      selected = await work();
      toast.success(message);
      await refresh();
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "That change could not be saved. Try again."); }
    finally { busy = ""; }
  }
  const answer = (service: ArtistLinkService, same: boolean, scope: ArtistLinkScope) => change(service,
    () => artistLinks.set(selected!.id, service, same, scope),
    same ? `${service.providerName}'s ${service.name} now joins your ${selected!.name}.` : `${service.providerName}'s ${service.name} is kept apart.`);
  const undo = (service: ArtistLinkService) => change(service, () => artistLinks.undo(selected!.id, service, service.scope ?? "household"),
    "Decision undone. Shared songs decide again.");
  async function checkAll() {
    if (checking) return;
    checking = true;
    try {
      const result = await artistLinks.review();
      toast.success(result.queued ? "Checking every library artist on your services. This runs in the background." : "Allstarr is already checking your artists.");
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The check could not start. Try again."); }
    finally { checking = false; }
  }

  // Searching updates as you type, once typing pauses.
  $effect(() => {
    const value = searchInput.trim();
    if (value === untrack(() => search)) return;
    const timer = setTimeout(() => { search = value; }, 350);
    return () => clearTimeout(timer);
  });
  $effect(() => { filter; search; untrack(() => { data = null; loading = true; void refresh(); }); });
  const scheduler = createRefreshScheduler(refresh, 250, 5000);
  onMount(() => {
    void home.schema().then(schema => providers = schema.providers).catch(() => {});
    if (initialArtist) void openArtist(initialArtist);
    const unsubscribe = liveUpdates.subscribe(() => { if (document.visibilityState !== "hidden") scheduler.schedule(); }, 10000);
    return () => { ++listRevision; ++panelRevision; panelController?.abort(); unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="artist-links-page" class:panel-open={open} aria-busy={loading}>
  <header class="artists-heading">
    <div class="artists-title"><h2>Matches</h2><MatchKindSwitch active="artists" /><Button variant="outline" size="sm" disabled={checking} onclick={() => void checkAll()}>Check all artists again</Button></div>
    {#if counts}<PageStatus label="Artist links" items={[
      { label: countLabel(counts.all, "library artist"), emphasis: true },
      { label: `${counts.linked.toLocaleString()} linked by shared songs` },
      { label: `${counts.needsLook.toLocaleString()} ${counts.needsLook === 1 ? "needs" : "need"} a look`, tone: counts.needsLook ? "warn" : "neutral" },
      { label: `${counts.decided.toLocaleString()} decided by you`, hideOnPhone: true },
    ]} />{/if}
  </header>
  <div class="artists-controls">
    <FilterSegments bind:value={filter} storageKey="matches.artists.filter" label="Artist filters" options={[
      { id: "needs-look", label: "Needs a look", count: counts?.needsLook },
      { id: "linked", label: "Linked", count: counts?.linked },
      { id: "decided", label: "By you", count: counts?.decided },
      { id: "apart", label: "Kept apart", count: counts?.apart },
      { id: "all", label: "All", count: counts?.all },
    ]} />
    <SearchField class="artists-search" bind:value={searchInput} label="Search artists" placeholder="Search artists" hiddenLabel />
  </div>
  {#if error}<ErrorState title="Artists could not load" detail={error} onretry={() => refresh()} />{/if}
  {#if loading && !data}<RowsSkeleton count={6} />
  {:else if rows.length}
    <div class="artist-list">
      {#each rows as row (row.id)}{@const state = artistPill(row.status)}
        <ItemRow title={row.name} subtitle={artistSummary(row)} attention={row.status === "needs-look"} selected={open && selected?.id === row.id} actionLabel={`Open ${row.name}`} onclick={() => openArtist(row.id, row)}>
          {#snippet leading()}<MediaArtwork class="artist-art" url={row.imageUrl} fallback={row.name.trim()[0]?.toUpperCase() ?? "?"} />{/snippet}
          {#snippet chips()}{#if row.services.length}<span class="services" aria-label={`On ${row.services.map(service => service.providerName).join(", ")}`}>
            {#each row.services as service (`${service.providerId}:${service.artistId}`)}<span class:dim={service.state === "candidate" || service.state === "not-same"}><ProviderMark decorative id={service.providerId} definition={findProviderDefinition(providers, service.providerId)} label={service.providerName} /></span>{/each}
          </span>{/if}{/snippet}
          {#snippet pill()}<StatePill compact tone={state.tone} text={state.text} />{/snippet}
        </ItemRow>
      {/each}
    </div>
    {#if data?.nextCursor}<Button variant="text" disabled={loadingMore} onclick={() => void more()}>Load more</Button>{/if}
  {:else if !error && data}
    <EmptyState title={search ? "No artists match this search" : filter === "needs-look" ? "Nothing needs you right now" : "No artists in this filter"}
      description={filter === "needs-look" && !search ? "Artists that share songs link on their own. Allstarr asks here only about same-named artists that don't." : "Try another filter or search."} />
  {/if}
</section>
<ArtistLinkPanel bind:open artist={selected} {providers} loading={panelLoading} error={panelError} {busy} onanswer={answer} onundo={undo}
  onretry={() => selected && openArtist(selected.id, selected)}
  onPrevious={selectedIndex > 0 ? () => openArtist(rows[selectedIndex - 1].id, rows[selectedIndex - 1]) : undefined}
  onNext={selectedIndex >= 0 && selectedIndex < rows.length - 1 ? () => openArtist(rows[selectedIndex + 1].id, rows[selectedIndex + 1]) : undefined} />

<style>
  .artist-links-page { display: grid; min-width: 0; align-content: start; gap: var(--space-4); }
  .artists-heading { display: grid; gap: var(--space-3); }
  .artists-title { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
  .artists-title h2 { margin: 0; font-size: var(--text-section); }
  .artists-title > :global(:last-child) { margin-left: auto; }
  .artists-controls { display: grid; gap: var(--space-3); min-width: 0; }
  .artists-controls :global(.artists-search) { max-width: 420px; }
  .artist-list { min-width: 0; overflow: hidden; border: 1px solid var(--color-edge); border-radius: 14px; }
  .artist-list :global(.artist-art) { border-radius: 50%; }
  .services { display: inline-flex; gap: var(--space-1); align-items: center; }
  .services :global(.provider-mark) { width: 18px; height: 18px; }
  .services .dim { opacity: .35; }
  @media (max-width: 599px) { .artists-title:has(:global(.match-kind)) h2 { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; } }
</style>
