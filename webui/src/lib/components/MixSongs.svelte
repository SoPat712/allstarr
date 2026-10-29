<script lang="ts">
  import { fade } from "svelte/transition";
  import type { MixSong } from "$lib/api";
  import { artworkUrl, reasonLabel } from "$lib/for-you";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Badge } from "$lib/components/ui/badge";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  let { songs, busy = false, onfeedback }: { songs: MixSong[]; busy?: boolean; onfeedback?: (row: MixSong, kind: "moreLikeThis" | "notForMe" | "neverArtist", today?: boolean) => void } = $props();
</script>
<ol class="mix-songs" aria-label="Songs">
  {#each songs as row (row.song.id)}
    <li out:fade={{ duration: typeof window !== "undefined" && window.matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160 }}>
      <MediaArtwork url={artworkUrl(row.song.id)} />
      <div class="song-info"><strong>{row.song.title}</strong><small>{row.song.artist}{row.song.album ? ` · ${row.song.album}` : ""}</small><Badge tone="neutral" class="reason-chip">{reasonLabel(row.reason)}</Badge></div>
      {#if onfeedback}<DropdownMenu.Root>
        <DropdownMenu.Trigger class="icon-button" disabled={busy} aria-label={`Actions for ${row.song.title}`}><UiIcon name="more" /></DropdownMenu.Trigger>
        <DropdownMenu.Portal><DropdownMenu.Content class="bits-menu song-menu" sideOffset={6} align="end">
          <DropdownMenu.Sub><DropdownMenu.SubTrigger class="bits-menu-item">More like this</DropdownMenu.SubTrigger><DropdownMenu.Portal><DropdownMenu.SubContent class="bits-menu song-menu"><DropdownMenu.Item class="bits-menu-item" onSelect={() => onfeedback?.(row, "moreLikeThis", true)}>Today only</DropdownMenu.Item><DropdownMenu.Item class="bits-menu-item" onSelect={() => onfeedback?.(row, "moreLikeThis", false)}>Until I change it</DropdownMenu.Item></DropdownMenu.SubContent></DropdownMenu.Portal></DropdownMenu.Sub>
          <DropdownMenu.Item class="bits-menu-item" onSelect={() => onfeedback?.(row, "notForMe")}>Not for me</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" onSelect={() => onfeedback?.(row, "neverArtist")}>Don't play this artist</DropdownMenu.Item>
        </DropdownMenu.Content></DropdownMenu.Portal>
      </DropdownMenu.Root>{/if}
    </li>
  {/each}
</ol>
<style>
  .song-info :global(.reason-chip) { text-transform: none; }
  :global(.song-menu .bits-menu-item) { min-height: 44px; }
  .mix-songs { list-style: none; padding: 0; margin: 0; }
  li { display: flex; align-items: center; gap: var(--space-3); padding-block: var(--space-3); min-width: 0; }
  li + li { border-top: 1px solid var(--color-edge); }
  li :global(.media-art) { flex: 0 0 44px; width: 44px; height: 44px; }
  .song-info { flex: 1; min-width: 0; }
  strong, small { display: block; overflow-wrap: anywhere; }
  strong { font-weight: 600; }
  small { color: var(--color-ink-muted); margin-block: var(--space-1); }
  .song-info :global(.badge) { white-space: normal; }
</style>
