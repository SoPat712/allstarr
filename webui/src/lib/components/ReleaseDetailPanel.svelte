<script lang="ts">
  import type { FollowedReleaseArtist, ReleaseInboxEntry } from "$lib/api";
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
</script>
<SidePanel open={true} title={entry?.title ?? "Release unavailable"} description={entry ? `${entry.artist} · ${type} · ${entry.isExplicit === true ? "Explicit" : entry.isExplicit === false ? "Clean" : "Explicit status unknown"}` : "This release is no longer in your inbox."} {onclose}>
  {#snippet artwork()}{#if entry}<MediaArtwork url={entry.artworkUrl} />{/if}{/snippet}
  {#snippet header()}{#if entry}Released {releaseDate(entry.releaseDate)}{entry.edition ? ` · ${entry.edition}` : ""}{/if}{/snippet}
  {#if entry}
    <div class="release-cover"><MediaArtwork url={entry.artworkUrl} /></div>
    <div class="follow-state">{#if followed}<StatePill tone="ok" text={`Following ${entry.artist}`} /><Button variant="text" class="destructive" disabled={busy} onclick={() => onunfollow(followed!)}>Unfollow</Button>{:else}<span>{entry.artist}</span><Button variant="outline" disabled={busy} onclick={onfind}>Follow artist</Button>{/if}</div>
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
  .release-sources { margin-top: var(--space-5); }
  h3 { margin-bottom: var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; text-transform: uppercase; letter-spacing: .05em; }
  p { color: var(--color-ink-muted); font-size: var(--text-sm); }
  :global(.destructive) { color: var(--danger); }
</style>
