<script lang="ts">
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import { artworkUrl, mosaicArtwork } from "$lib/for-you";
  let { references = [], dim = false }: { references?: string[]; dim?: boolean } = $props();
  const covers = $derived(mosaicArtwork(references));
</script>
<span class="mix-artwork" class:mosaic={covers.length === 4} class:dim>
  {#if covers.length}{#each covers as id, i (`${i}:${id}`)}<MediaArtwork url={artworkUrl(id)} />{/each}
  {:else}<MediaArtwork />{/if}
</span>
<style>
  .mix-artwork { display: grid; width: 100%; aspect-ratio: 1; border-radius: var(--radius-md); overflow: hidden; background: var(--color-panel-raised); }
  .mosaic { grid-template: repeat(2, minmax(0, 1fr)) / repeat(2, minmax(0, 1fr)); }
  .mix-artwork :global(.media-art) { width: 100%; height: 100%; border-radius: 0; font-size: 2.5rem; }
  .dim { opacity: .55; }
</style>
