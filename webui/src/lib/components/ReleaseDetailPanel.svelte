<script lang="ts">
  import { releaseInbox, type FollowedReleaseArtist, type ReleaseInboxEntry, type ReleaseTracks } from "$lib/api";
  import { formatDuration } from "$lib/playlists";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import { releaseDate } from "$lib/intelligence";
  import { humanize } from "$lib/activity";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import { Button } from "$lib/components/ui/button";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  let { entry, artists, busy = false, onclose, onunfollow, ondismiss, onfind }: {
    entry?: ReleaseInboxEntry; artists: FollowedReleaseArtist[]; busy?: boolean; onclose: () => void;
    onunfollow: (artist: FollowedReleaseArtist) => void; ondismiss: (entry: ReleaseInboxEntry) => void; onfind: () => void;
  } = $props();
  const followed = $derived(artists.find(artist => artist.displayName.toLowerCase() === entry?.artist.toLowerCase()));
  const type = $derived(({ album: "Album", single: "Single", ep: "EP" } as Record<string, string>)[entry?.type.toLowerCase() ?? ""] ?? "Release");
  // The track list loads after the panel opens; the first six show and the rest fold away.
  let tracks = $state<ReleaseTracks | null>(null), tracksFailed = $state(false), allTracks = $state(false);
  let request = 0;
  $effect(() => {
    const id = entry?.id;
    tracks = null; tracksFailed = false; allTracks = false;
    if (!id) return;
    const current = ++request;
    releaseInbox.tracks(id).then(value => {
      if (current === request) tracks = { providerId: value?.providerId ?? null, tracks: Array.isArray(value?.tracks) ? value.tracks : [] };
    }).catch(() => { if (current === request) tracksFailed = true; });
  });
  const shownTracks = $derived(tracks ? allTracks ? tracks.tracks : tracks.tracks.slice(0, 6) : []);
</script>
<SidePanel open={true} title={entry?.title ?? "Release unavailable"} description={entry ? `${entry.artist} · ${type} · ${entry.isExplicit === true ? "Explicit" : entry.isExplicit === false ? "Clean" : "Explicit status unknown"}` : "This release is no longer in your inbox."} {onclose}>
  {#snippet artwork()}{#if entry}<MediaArtwork url={entry.artworkUrl} />{/if}{/snippet}
  {#snippet header()}{#if entry}Released {releaseDate(entry.releaseDate)}{entry.edition ? ` · ${entry.edition}` : ""}{/if}{/snippet}
  {#if entry}
    <div class="release-cover"><MediaArtwork url={entry.artworkUrl} /></div>
    <div class="follow-state">{#if followed}<StatePill tone="ok" text={`Following ${entry.artist}`} /><Button variant="text" class="destructive" disabled={busy} onclick={() => onunfollow(followed!)}>Unfollow</Button>{:else}<span>{entry.artist}</span><Button variant="outline" disabled={busy} onclick={onfind}>Follow artist</Button>{/if}</div>
    <section class="release-tracks" aria-label="Tracks"><h3>Tracks</h3>
      {#if tracks?.providerId}<p class="tracks-source">From {humanize(tracks.providerId)}</p>{/if}
      {#if tracksFailed}<p>The track list could not be loaded.</p>
      {:else if !tracks}<RowsSkeleton count={4} label="Loading tracks" />
      {:else if !tracks.tracks.length}<p>No source lists this release's tracks yet.</p>
      {:else}<ol>{#each shownTracks as track (track.number + track.title)}<li><span class="track-number">{track.number}</span><span class="track-title"><strong>{track.title}{#if track.isExplicit} <abbr title="Explicit">E</abbr>{/if}</strong><small>{track.artists}</small></span><span class="track-time">{formatDuration(track.durationMs)}</span></li>{/each}</ol>
        {#if tracks.tracks.length > 6}<Button variant="text" size="sm" onclick={() => allTracks = !allTracks}>{allTracks ? "Show fewer tracks" : `${tracks.tracks.length - 6} more tracks`}</Button>{/if}{/if}
    </section>
    <section class="release-sources" aria-label="Available from"><h3>Available from</h3>
      {#each entry.sources ?? [] as source, index (`${index}:${source.providerId}:${source.albumId}`)}<ItemRow title={humanize(source.providerId)} subtitle={`${source.title}${source.edition ? ` · ${source.edition}` : ""}`} />{:else}<p>Source details aren't available for this release.</p>{/each}
    </section>
  {:else}<EmptyState title="This release is no longer in your inbox" description="Close this panel to browse your other releases." />{/if}
  {#snippet footer()}{#if entry}<Button variant="text" class="destructive" disabled={busy} onclick={() => ondismiss(entry!)}>Dismiss release</Button>{/if}{/snippet}
</SidePanel>
<style>
  .release-cover { width: min(200px, 60%); margin: 0 auto var(--space-4); }
  .release-cover :global(.media-art) { width: 100%; height: auto; aspect-ratio: 1; }
  .follow-state { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-2); border-block: 1px solid var(--color-edge); padding-block: var(--space-3); }
  .release-sources, .release-tracks { margin-top: var(--space-5); }
  .release-tracks ol { display: grid; margin: 0; padding: 0; list-style: none; }
  .release-tracks li { display: grid; grid-template-columns: 1.5rem minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); min-height: 48px; border-bottom: 1px solid var(--color-edge); }
  .track-number, .track-time { color: var(--color-ink-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; }
  .track-title { display: grid; min-width: 0; }
  .track-title strong, .track-title small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .track-title strong { font-size: var(--text-sm); font-weight: 500; }
  .track-title small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .track-title abbr { margin-left: var(--space-1); padding: 0 4px; border-radius: 3px; background: var(--color-panel-raised); color: var(--color-ink-muted); font-size: .65rem; text-decoration: none; }
  .tracks-source { margin: calc(-1 * var(--space-2)) 0 var(--space-2); font-size: var(--text-xs) !important; }
  h3 { margin-bottom: var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; text-transform: uppercase; letter-spacing: .05em; }
  p { color: var(--color-ink-muted); font-size: var(--text-sm); }
  :global(.destructive) { color: var(--danger); }
</style>
