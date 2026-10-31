<script lang="ts">
  import { onMount, tick } from "svelte";
  import { X } from "@lucide/svelte";
  import { releaseInbox, mixes, type FollowedReleaseArtist, type ReleaseInbox, type ReleaseInboxEntry } from "$lib/api";
  import { groupByWeek, releaseDate } from "$lib/intelligence";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";

  let { initialRelease = "" }: { initialRelease?: string } = $props();
  let radar = $state("");
  let inbox = $state<ReleaseInbox | null>(null), artists = $state<FollowedReleaseArtist[]>([]);
  let loading = $state(true), error = $state(""), action = $state("");
  let artistsOpen = $state(false), followOpen = $state(false), followError = $state("");
  let pending: Promise<void> | null = null, disposed = false;
  const groups = $derived(groupByWeek(inbox?.entries ?? [], entry => entry.releaseDate));
  const unchecked = $derived(inbox?.coverage.filter(item => item.status !== "known") ?? []);
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
  async function follow() {
    if (action) return;
    action = "follow"; followError = "";
    try {
      await pending;
      const result = await releaseInbox.followFromHearts();
      if (disposed) return;
      followOpen = false;
      toast.success(`Following ${result.followed} new artists`);
      await load();
    } catch { if (!disposed) followError = "Artists could not be followed. Try again."; }
    finally { if (!disposed) action = ""; }
  }
  async function unfollow(artist: FollowedReleaseArtist) {
    if (action) return;
    action = `artist:${artist.providerId}:${artist.artistId}`;
    try {
      await pending;
      await releaseInbox.unfollow(artist.providerId, artist.artistId);
      if (disposed) return;
      artists = artists.filter(item => item.providerId !== artist.providerId || item.artistId !== artist.artistId);
      toast.success(`Unfollowed ${artist.displayName}.`);
      await load();
    } catch { if (!disposed) toast.error("The artist could not be unfollowed. Try again."); }
    finally { if (!disposed) action = ""; }
  }
  async function dismiss(entry: ReleaseInboxEntry) {
    if (action) return;
    action = entry.id;
    try {
      await pending;
      await releaseInbox.dismiss(entry.id);
      if (!disposed && inbox) inbox = { ...inbox, entries: inbox.entries.filter(item => item.id !== entry.id) };
    } catch { if (!disposed) toast.error("The release could not be dismissed. Try again."); }
    finally { if (!disposed) action = ""; }
  }
  $effect(() => {
    const release = initialRelease;
    if (!loading && release) void tick().then(() => document.getElementById(`release-${release}`)?.scrollIntoView({ block: "center", behavior: "instant" }));
  });
  const scheduler = createRefreshScheduler(async () => { if (!action) await load(); });
  onMount(() => {
    void load();
    void mixes.list().then(cards => { if (!disposed) radar = cards.find(card => card.kind === "release-radar")?.id ?? ""; }).catch(() => {});
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

<div class="releases-inbox">
  <div class="section-heading">
    <p>{#if radar}<a href={`#/for-you?mix=${encodeURIComponent(radar)}`}>Just Dropped collects these every Friday.</a>{:else}Just Dropped collects these every Friday.{/if}</p>
    <div class="release-actions">
      <Button variant="secondary" onclick={() => artistsOpen = true}>Following ({artists.length})</Button>
      <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label="Release actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="bits-menu release-menu" align="end"><DropdownMenu.Item class="bits-menu-item" onSelect={() => { followError = ""; followOpen = true; }}>Follow artists you play a lot</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
      <RefreshButton variant="secondary" label="Refresh releases" disabled={Boolean(action)} onrefresh={load} />
    </div>
  </div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading new releases" />
  {:else if inbox}
    {#if unchecked.length}
      <details class="settings-card release-disclosure">
        <summary class="disclosure-summary"><DisclosureLabel title={`We couldn't check ${unchecked.length} ${unchecked.length === 1 ? "artist" : "artists"}`} /></summary>
        <p>Some releases may be missing.</p>
        <ul>{#each unchecked as item}<li>{item.artist.displayName}</li>{/each}</ul>
      </details>
    {/if}
    {#if !inbox.entries.length}
      <div class="settings-card release-empty"><p>Follow artists to see their new music here.</p></div>
    {:else}
      {#each groups as group (group.label)}
        <section aria-label={group.label}>
          <h2>{group.label}</h2>
          <div class="release-cards">
            {#each group.items as entry (entry.id)}
              <article id={`release-${entry.id}`} class="settings-card release-card" aria-label={`${entry.title} by ${entry.artist}`}>
                <MediaArtwork class="release-art" fallback="♫" />
                <div class="release-info">
                  <h3>{entry.title}</h3><p>{entry.artist}</p>
                  <p>{releaseType(entry.type)} · {entry.isExplicit === true ? "Explicit" : entry.isExplicit === false ? "Clean" : "Explicit status unknown"}</p>
                  <p><time datetime={entry.releaseDate ?? undefined}>{releaseDate(entry.releaseDate)}</time>{entry.edition ? ` · ${entry.edition}` : ""}</p>
                </div>
                <button class="icon-button" type="button" aria-label={`Dismiss ${entry.title}`} disabled={Boolean(action)} onclick={() => void dismiss(entry)}><X size={18} aria-hidden="true" /></button>
              </article>
            {/each}
          </div>
        </section>
      {/each}
    {/if}
    {#if hidden.length}
      <details class="settings-card release-disclosure">
        <summary class="disclosure-summary"><DisclosureLabel title={`Hidden: ${hidden.length} ${hidden.length === 1 ? "release" : "releases"}`} /></summary>
        <ul>{#each hidden as entry}<li><strong>{entry.title}</strong> · {entry.artist} · {hiddenReason(entry.exclusionReason)}</li>{/each}</ul>
      </details>
    {/if}
  {/if}
</div>

<Dialog.Root bind:open={artistsOpen}>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog releases-artists-dialog">
      <header>
        <div><Dialog.Title>Followed artists ({artists.length})</Dialog.Title><Dialog.Description>Artists you follow for new releases.</Dialog.Description></div>
        <Dialog.Close class="icon-button" aria-label="Close followed artists"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <div class="artists-body">
        {#if artists.length}
          <ul class="followed-artists">{#each artists as artist (`${artist.providerId}:${artist.artistId}`)}
            <li><strong>{artist.displayName}</strong><Button variant="secondary" disabled={Boolean(action)} aria-label={`Unfollow ${artist.displayName}`} onclick={() => void unfollow(artist)}>Unfollow</Button></li>
          {/each}</ul>
        {:else}<p>You aren't following any artists yet.</p>{/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close></footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
<ConfirmDialog bind:open={followOpen} title="Follow artists you play a lot" description="Follow up to 200 artists from songs you've hearted? Following from your play history isn't available yet." confirmLabel="Follow artists from my hearts" confirmVariant="default" disabled={Boolean(action)} closeOnConfirm={false} error={followError} onConfirm={follow} />

<style>
  :global(.release-menu .bits-menu-item) { min-height: 44px; }
  .releases-inbox { display: grid; gap: var(--space-6); min-width: 0; }
  .section-heading, .release-actions { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .section-heading p, .release-info p, .release-disclosure p { color: var(--color-ink-muted); }
  h2 { margin-bottom: var(--space-4); font-size: 1.125rem; font-weight: 600; }
  h3 { font-weight: 600; overflow-wrap: anywhere; }
  .release-cards { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--section-gap); }
  .release-card { display: flex; align-items: start; gap: var(--space-3); padding: var(--surface-gutter); min-width: 0; }
  .release-card :global(.release-art) { width: 64px; height: 64px; flex: 0 0 64px; }
  .release-info { flex: 1; min-width: 0; overflow-wrap: anywhere; }
  .release-info p { margin-top: var(--space-1); font-size: var(--text-sm); }
  .release-disclosure, .release-empty { padding: var(--surface-gutter); }
  .release-disclosure ul { display: grid; gap: var(--space-2); margin-top: var(--space-3); padding-left: var(--space-5); overflow-wrap: anywhere; }
  .release-disclosure p { margin-top: var(--space-3); }
  .release-disclosure summary { display: flex; align-items: center; min-height: 44px; cursor: pointer; }
  .artists-body { overflow-y: auto; padding: var(--surface-gutter); }
  .followed-artists { display: grid; gap: var(--space-3); list-style: none; padding: 0; margin: 0; }
  .followed-artists li { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .followed-artists strong { overflow-wrap: anywhere; min-width: 0; }
</style>
