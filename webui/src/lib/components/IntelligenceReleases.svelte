<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import { onMount, type Component } from "svelte";
  import { goto } from "$app/navigation";
  import { X } from "@lucide/svelte";
  import { releaseInbox, type FollowedReleaseArtist, type ReleaseInbox, type ReleaseInboxEntry } from "$lib/api";
  import { releaseDate, releaseArtistImage } from "$lib/intelligence";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import ReleaseArtistChoice from "$lib/components/ReleaseArtistChoice.svelte";

  let { initialRelease = "", administrator = false }: { initialRelease?: string; administrator?: boolean } = $props();
  let inbox = $state<ReleaseInbox | null>(null), artists = $state<FollowedReleaseArtist[]>([]);
  let loading = $state(true), error = $state(""), pendingAction = $state(""), checking = $state(false);
  let artistsOpen = $state(false), followOpen = $state(false), followError = $state("");
  let pending: Promise<void> | null = null, disposed = false;
  let type = $state("all"), sort = $state("newest");
  let ReleasePanel = $state<Component<any>>();
  let dismissTarget = $state<ReleaseInboxEntry | null>(null), unfollowTarget = $state<FollowedReleaseArtist | null>(null);
  let dismissOpen = $state(false), unfollowOpen = $state(false), choiceOpen = $state(false);
  let choiceTarget = $state<FollowedReleaseArtist | null>(null);
  const selected = $derived(inbox?.entries.find(entry => entry.id === initialRelease) ?? inbox?.excluded?.find(entry => entry.id === initialRelease));
  const entries = $derived((inbox?.entries ?? []).filter(entry => type === "all" || entry.type.toLowerCase() === type).slice().sort((a, b) => (a.releaseDate ?? "").localeCompare(b.releaseDate ?? "") * (sort === "newest" ? -1 : 1)));
  const filters = $derived([{ id: "all", label: "All", count: inbox?.entries.length ?? 0 }, ...[{ id: "album", label: "Albums" }, { id: "single", label: "Singles" }, { id: "ep", label: "EPs" }].map(option => ({ ...option, count: inbox?.entries.filter(entry => entry.type.toLowerCase() === option.id).length ?? 0 }))]);
  const closeRelease = () => { void goto("#/for-you?section=releases", { keepFocus: true, noScroll: true }); };
  const openRelease = (entry: ReleaseInboxEntry) => { void goto(`#/for-you?section=releases&release=${encodeURIComponent(entry.id)}`, { keepFocus: true, noScroll: true }); };
  function askDismiss(entry: ReleaseInboxEntry) { dismissTarget = entry; dismissOpen = true; }
  function askUnfollow(artist: FollowedReleaseArtist) { unfollowTarget = artist; unfollowOpen = true; }
  function artistReason(artist: FollowedReleaseArtist) {
    return artist.resolutionReason || (artist.libraryArtistId ? inbox?.coverage.find(item => item.artist.libraryArtistId === artist.libraryArtistId)?.reason : "") || "";
  }
  const needsArtistChoice = (artist: FollowedReleaseArtist) => Boolean(artist.resolutionReason) || (artist.providerId === "jellyfin" && !artist.listenerConfirmed);
  function askArtistChoice(artist: FollowedReleaseArtist) { choiceTarget = artist; choiceOpen = true; }
  async function chooseArtist(artist: FollowedReleaseArtist, providerId: string | null, artistId: string | null) {
    if (pendingAction || !artist.libraryArtistId) return;
    pendingAction = `choice:${artist.libraryArtistId}`;
    try {
      await pending;
      const saved = await releaseInbox.chooseArtist(artist.libraryArtistId, providerId, artistId);
      if (disposed) return;
      artists = artists.map(item => item.libraryArtistId === artist.libraryArtistId ? saved : item);
      await load();
      if (!disposed) toast.success(`Artist choice saved for ${artist.displayName}`);
    } finally { if (!disposed) pendingAction = ""; }
  }
  async function loadReleasePanel() {
    try { ReleasePanel = (await import("$lib/components/ReleaseDetailPanel.svelte")).default; }
    catch { error = "The release panel could not be loaded. Try again."; }
  }
  $effect(() => { if (initialRelease && !ReleasePanel) void loadReleasePanel(); });
  const unchecked = $derived(inbox?.coverage.filter(item => item.status !== "known" && item.status !== "library-only") ?? []);
  const hidden = $derived(inbox?.excluded ?? []);


  function hiddenReason(reason: string | null) {
    return ({ compilation: "Compilation", reissue: "Reissue", appearance: "Guest appearance", "date-unknown": "Date unavailable" } as Record<string, string>)[reason ?? ""] ?? "Excluded release";
  }
  function releaseType(type: string) {
    return ({ album: "Album", ep: "EP", single: "Single" } as Record<string, string>)[type.toLowerCase()] ?? "Release";
  }

  async function load() {
    if (pending) return pending;
    pending = (async () => {
      try {
        const [nextInbox, nextArtists] = await Promise.all([releaseInbox.get(), releaseInbox.artists()]);
        if (!disposed) { inbox = nextInbox; artists = nextArtists; error = ""; }
      } catch { if (!disposed) error = "New releases could not be loaded. Refresh to try again."; }
      finally { if (!disposed) loading = false; pending = null; }
    })();
    return pending;
  }
  async function checkAgain() {
    if (pendingAction || checking) return;
    checking = true;
    try {
      if ((await releaseInbox.checkNow()).checking) {
        // The check paces itself a couple of seconds per artist, so read the results as they land.
        for (const wait of [3000, 10000, 20000]) {
          await new Promise(resolve => setTimeout(resolve, wait));
          if (disposed) return;
          await load();
        }
      } else await load();
    } catch { await load(); }
    finally { if (!disposed) checking = false; }
  }
  async function follow() {
    if (pendingAction) return;
    pendingAction = "follow"; followError = "";
    try {
      await pending;
      const result = await releaseInbox.followFromHearts();
      if (disposed) return;
      followOpen = false;
      toast.success(`Following ${countLabel(result.followed, "new artist")}`);
      await load();
    } catch { if (!disposed) followError = "Artists could not be followed. Try again."; }
    finally { if (!disposed) pendingAction = ""; }
  }
  let artistQuery = $state(""), found = $state<FollowedReleaseArtist[]>([]), searching = $state(false);
  // Looks artists up once typing pauses; older answers are dropped if the search has moved on.
  let searchRequest = 0;
  $effect(() => {
    const query = artistQuery.trim();
    if (query.length < 2) { found = []; searching = false; return; }
    searching = true;
    const request = ++searchRequest;
    const timer = setTimeout(async () => {
      try { const next = await releaseInbox.search(query); if (request === searchRequest) found = next; }
      catch { if (request === searchRequest) found = []; }
      finally { if (request === searchRequest) searching = false; }
    }, 300);
    return () => clearTimeout(timer);
  });
  const isFollowed = (artist: FollowedReleaseArtist) => artists.some(item => item.providerId === artist.providerId && item.artistId === artist.artistId);
  async function followArtist(artist: FollowedReleaseArtist) {
    if (pendingAction) return;
    pendingAction = `follow:${artist.artistId}`;
    try {
      await releaseInbox.follow(artist);
      if (disposed) return;
      toast.success(`Following ${artist.displayName}`);
      await load();
    } catch { if (!disposed) toast.error(`${artist.displayName} could not be followed. Try again.`); }
    finally { if (!disposed) pendingAction = ""; }
  }
  async function followTop() {
    if (pendingAction) return;
    pendingAction = "follow-top";
    try {
      await pending;
      const result = await releaseInbox.followFromHistory();
      if (disposed) return;
      if (result.followed) toast.success(`Following ${result.followed} of your top artists`);
      else toast.error("No more artists to follow from your listening yet.");
      await load();
    } catch { if (!disposed) toast.error("Your top artists could not be followed. Try again."); }
    finally { if (!disposed) pendingAction = ""; }
  }
  async function unfollow(artist: FollowedReleaseArtist) {
    if (pendingAction) return;
    pendingAction = `artist:${artist.providerId}:${artist.artistId}`;
    try {
      await pending;
      await releaseInbox.unfollow(artist.providerId, artist.artistId, artist.libraryArtistId);
      if (disposed) return;
      artists = artists.filter(item => artist.libraryArtistId ? item.libraryArtistId !== artist.libraryArtistId :
        item.libraryArtistId != null || item.providerId !== artist.providerId || item.artistId !== artist.artistId);
      toast.success(`Unfollowed ${artist.displayName}.`);
      await load();
      unfollowOpen = false;
    } catch { if (!disposed) toast.error("The artist could not be unfollowed. Try again."); }
    finally { if (!disposed) pendingAction = ""; }
  }
  async function dismiss(entry: ReleaseInboxEntry) {
    if (pendingAction) return;
    pendingAction = entry.id;
    try {
      await pending;
      await releaseInbox.dismiss(entry.id);
      if (!disposed && inbox) { inbox = { ...inbox, entries: inbox.entries.filter(item => item.id !== entry.id) }; dismissOpen = false; if (initialRelease === entry.id) closeRelease(); }
    } catch { if (!disposed) toast.error("The release could not be dismissed. Try again."); }
    finally { if (!disposed) pendingAction = ""; }
  }
  const scheduler = createRefreshScheduler(async () => { if (!pendingAction) await load(); });
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

<div class="releases-inbox">
  <PageStatus items={[{ label: `${inbox?.entries.length ?? 0} ${(inbox?.entries.length ?? 0) === 1 ? "release" : "releases"} from ${countLabel(artists.length, "artist")} you follow` }, { label: "Just Dropped collects these every Friday." }]} />
  <section class="following-row" aria-label="Following artists">
    <Button variant="text" size="sm" onclick={() => artistsOpen = true}>Following ({artists.length})</Button>
    <div class="artist-faces">{#each artists.slice(0, 6) as artist, index (`${index}:${artist.providerId}:${artist.artistId}`)}<button type="button" onclick={() => artistsOpen = true} aria-label={`Manage ${artist.displayName}`}><MediaArtwork url={releaseArtistImage(artist)} /><span>{artist.displayName}</span></button>{/each}</div>
    <Button variant="outline" size="sm" onclick={() => artistsOpen = true}>Follow more artists</Button>
  </section>
  <div class="release-toolbar">
    <FilterSegments options={filters} bind:value={type} storageKey="allstarr.releases.type" label="Release type" />
    <div class="release-actions"><SelectField label="Release order" bind:value={sort} options={[{ value: "newest", label: "Newest first" }, { value: "oldest", label: "Oldest first" }]} />
      <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label="Release actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu release-menu" align="end"><DropdownMenu.Item class="bits-menu-item" onSelect={() => void followTop()}>Follow my top artists</DropdownMenu.Item><DropdownMenu.Item class="bits-menu-item" onSelect={() => { followError = ""; followOpen = true; }}>Follow artists from my hearts</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
    </div>
  </div>
  {#if error}<ErrorState title="Couldn't load new releases" detail={error} onretry={async () => { await load(); if (initialRelease && !ReleasePanel) await loadReleasePanel(); }} />{/if}
  {#if loading}<RowsSkeleton />{:else if inbox}
    {#if !inbox.entries.length}<EmptyState title="No new releases yet" description={artists.length ? "Nothing new from the artists you follow yet." : "Follow artists to see their new music here."}>
      {#snippet action()}{#if !artists.length}<Button variant="outline" disabled={Boolean(pendingAction)} onclick={() => void followTop()}>{pendingAction === "follow-top" ? "Finding your top artists…" : "Follow my top artists"}</Button>{/if}<Button variant="outline" onclick={() => artistsOpen = true}>Find an artist</Button>{/snippet}
    </EmptyState>
    {:else if !entries.length}<EmptyState title="No releases of this type" description="Choose another type to see more releases." />
    {:else}<div class="release-cards">{#each entries as entry, index (`${index}:${entry.id}`)}
      <article class="release-card" aria-label={`${entry.title} by ${entry.artist}`}>
        <button class="release-open" type="button" aria-label={`Open ${entry.title} by ${entry.artist}`} onclick={() => openRelease(entry)}>
          <MediaArtwork class="release-art" url={entry.artworkUrl} fallback="♫" />
          <h3>{entry.title}</h3><p>{entry.artist}</p><p>{releaseType(entry.type)} · {entry.isExplicit === true ? "Explicit" : entry.isExplicit === false ? "Clean" : "Explicit status unknown"}</p>
          <p><time datetime={entry.releaseDate ?? undefined}>{releaseDate(entry.releaseDate)}</time>{entry.edition ? ` · ${entry.edition}` : ""}</p>
        </button>
        <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button release-card-menu" aria-label={`Actions for ${entry.title}`}><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu release-menu" align="end"><DropdownMenu.Item class="bits-menu-item destructive" disabled={Boolean(pendingAction)} onSelect={() => askDismiss(entry)}>Dismiss {entry.title}</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
      </article>
    {/each}</div>{/if}
    {#if unchecked.length}<div class="checking-note"><AdvancedFold placement="flush" label={`Couldn't check ${countLabel(unchecked.length, "artist")}.`}><p>Some releases may be missing.</p><ul>{#each unchecked as item}<li>{item.artist.displayName}{#if item.reason} · {item.reason}{/if}</li>{/each}</ul></AdvancedFold><Button variant="text" size="sm" disabled={Boolean(pendingAction) || checking} onclick={() => void checkAgain()}>{checking ? "Checking…" : "Check again"}</Button></div>{/if}
    {#if hidden.length}<details class="release-disclosure"><summary class="disclosure-summary"><DisclosureLabel title={`Hidden: ${countLabel(hidden.length, "release")}`} /></summary><ul>{#each hidden as entry}<li><strong>{entry.title}</strong> · {entry.artist} · {hiddenReason(entry.exclusionReason)}</li>{/each}</ul></details>{/if}
  {/if}
</div>
{#if initialRelease && !loading && ReleasePanel}<ReleasePanel entry={selected} {artists} {administrator} busy={Boolean(pendingAction)} onclose={closeRelease} onunfollow={askUnfollow} ondismiss={askDismiss} onfind={() => artistsOpen = true} />{/if}
<ConfirmDialog bind:open={dismissOpen} title="Dismiss release?" description={`Remove ${dismissTarget?.title ?? "this release"} from your release inbox?`} confirmLabel="Dismiss release" closeOnConfirm={false} disabled={Boolean(pendingAction)} onConfirm={() => dismissTarget ? dismiss(dismissTarget) : undefined} />
<ConfirmDialog bind:open={unfollowOpen} title="Unfollow artist?" description={`Stop checking new releases from ${unfollowTarget?.displayName ?? "this artist"}?`} confirmLabel="Unfollow artist" closeOnConfirm={false} disabled={Boolean(pendingAction)} onConfirm={() => unfollowTarget ? unfollow(unfollowTarget) : undefined} />

<Dialog.Root bind:open={artistsOpen}>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog releases-artists-dialog">
      <header>
        <div><Dialog.Title>Followed artists ({artists.length})</Dialog.Title><Dialog.Description>Artists you follow for new releases.</Dialog.Description></div>
        <Dialog.Close class="icon-button" aria-label="Close followed artists"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <div class="artists-body">
        <SearchField bind:value={artistQuery} label="Find an artist to follow" placeholder="Find an artist to follow" hiddenLabel />
        {#if artistQuery.trim().length >= 2}
          <ul class="followed-artists found-artists" aria-label="Artists found">
            {#each found as artist (`${artist.providerId}:${artist.artistId}`)}
              <li><MediaArtwork url={`/api/admin/stats/art?${new URLSearchParams({ artist: artist.displayName })}`} class="found-art" /><strong>{artist.displayName}</strong>
                <Button variant="secondary" disabled={isFollowed(artist) || Boolean(pendingAction)} onclick={() => void followArtist(artist)}>{isFollowed(artist) ? "Following" : "Follow"}</Button></li>
            {:else}
              <li><span>{searching ? "Searching…" : "No artists found."}</span></li>
            {/each}
          </ul>
        {/if}
        {#if artists.length}
          <ul class="followed-artists">{#each artists as artist, index (`${index}:${artist.providerId}:${artist.artistId}`)}
            <li class="followed-artist">
              <div class="artist-copy"><strong>{artist.displayName}</strong>
                {#if artist.libraryArtistId && needsArtistChoice(artist)}<p>{artistReason(artist) || "Choose an artist to check releases from a music service."}</p>
                {:else if artist.libraryArtistId && artist.providerId === "jellyfin" && artist.listenerConfirmed}<p>No service artist selected.</p>{/if}
              </div>
              <div class="artist-controls">
                {#if artist.libraryArtistId}<Button variant={needsArtistChoice(artist) ? "outline" : "text"} disabled={Boolean(pendingAction)} aria-label={`${needsArtistChoice(artist) ? "Choose the right artist" : "Change artist"} for ${artist.displayName}`} onclick={() => askArtistChoice(artist)}>{needsArtistChoice(artist) ? "Choose the right artist" : "Change artist"}</Button>{/if}
                <Button variant="text" class="destructive" disabled={Boolean(pendingAction)} aria-label={`Unfollow ${artist.displayName}`} onclick={() => askUnfollow(artist)}>Unfollow</Button>
              </div>
            </li>
          {/each}</ul>
        {:else}<p>You aren't following any artists yet.</p>{/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close></footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
{#if choiceOpen && choiceTarget}<ReleaseArtistChoice bind:open={choiceOpen} artist={choiceTarget} reason={artistReason(choiceTarget)} onsave={(providerId, artistId) => chooseArtist(choiceTarget!, providerId, artistId)} />{/if}
<ConfirmDialog bind:open={followOpen} title="Follow artists from your hearts" description="Follow up to 200 artists from songs you've hearted?" confirmLabel="Follow artists from my hearts" confirmVariant="default" disabled={Boolean(pendingAction)} closeOnConfirm={false} error={followError} onConfirm={follow} />

<style>
  .releases-inbox { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--space-4); min-width: 0; }
  .following-row { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); background: var(--color-panel); }
  .artist-faces { display: flex; flex: 1; min-width: 0; gap: var(--space-4); overflow-x: auto; }
  .artist-faces button { display: grid; flex: 0 0 auto; justify-items: center; gap: var(--space-1); min-width: 44px; max-width: 88px; font-size: var(--text-xs); color: var(--color-ink-muted); }
  .artist-faces :global(.media-art) { width: 36px; height: 36px; border-radius: 50%; overflow: hidden; }
  .artist-faces span { width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .release-toolbar, .release-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-2); }
  .release-cards { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: var(--space-4); }
  .release-card { position: relative; min-width: 0; }
  .release-open { display: block; width: 100%; text-align: left; border-radius: var(--radius-md); }
  .release-open:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 3px; }
  .release-open :global(.release-art) { width: 100%; aspect-ratio: 1; height: auto; margin-bottom: var(--space-2); }
  .release-open h3 { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--text-sm); font-weight: 600; }
  .release-open p { margin-top: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); overflow-wrap: anywhere; }
  .release-card :global(.release-card-menu) { display: flex; margin-top: var(--space-1); margin-left: auto; color: var(--color-signal-text); }
  .checking-note { display: flex; flex-wrap: wrap; align-items: baseline; gap: var(--space-2); font-size: var(--text-sm); color: var(--color-ink-muted); }
  /* The note sits in a line of text, so its fold has no divider above it. */
  .checking-note > :global(.settings-fold) { border-top: 0; }
  .release-disclosure { padding: var(--space-3); border-top: 1px solid var(--color-edge); font-size: var(--text-sm); color: var(--color-ink-muted); }
  .release-disclosure summary { min-height: 44px; display: flex; align-items: center; cursor: pointer; }
  .release-disclosure ul, .checking-note ul { display: grid; gap: var(--space-2); margin-top: var(--space-2); padding-left: var(--space-4); overflow-wrap: anywhere; }
  .artists-body { overflow-y: auto; padding: var(--surface-gutter); }
  .followed-artists { display: grid; gap: var(--space-3); list-style: none; padding: 0; margin: 0; }
  .followed-artists li { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .followed-artists strong { overflow-wrap: anywhere; min-width: 0; }
  .followed-artists .followed-artist { flex-wrap: wrap; align-items: flex-start; }
  .artist-copy { flex: 1 1 9rem; min-width: 0; padding-top: var(--space-2); }
  .artist-copy p { margin-top: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; overflow-wrap: anywhere; }
  .artist-controls { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
  .artist-controls :global(button) { min-height: 44px; }
  .found-artists { margin-bottom: var(--space-4); padding-bottom: var(--space-4); border-bottom: 1px solid var(--color-edge); }
  .found-artists strong { flex: 1; }
  .found-artists :global(.found-art) { flex: none; width: 40px; height: 40px; border-radius: 50%; overflow: hidden; }
  :global(.release-menu .bits-menu-item) { min-height: 44px; }
  :global(.release-menu .destructive), .followed-artists :global(.destructive) { color: var(--danger); }
  @media (max-width: 1100px) { .release-cards { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
  @media (max-width: 599px) { .release-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); } .artist-faces { order: 1; flex-basis: 100%; } .artist-controls { width: 100%; justify-content: space-between; } }
</style>
