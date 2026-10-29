<script lang="ts">
  import { onMount } from "svelte";
  import type { MixCard, MixSong } from "$lib/api";
  import { coverFor } from "$lib/covers/cover-for";
  import { mountCover } from "$lib/covers/mount";
  import "$lib/covers/fonts.css";
  let { mix, songs }: { mix: MixCard; songs?: MixSong[] } = $props();
  const cover = $derived(coverFor(mix, songs));
  let canvas: HTMLCanvasElement;
  let mounted: ReturnType<typeof mountCover> | undefined;
  onMount(() => { mounted = mountCover(canvas, () => cover); return () => mounted?.destroy(); });
  $effect(() => { cover; mounted?.update(); });
</script>
<canvas bind:this={canvas} width="0" height="0" aria-hidden="true"></canvas>
<style>
  canvas { display: block; width: 100%; aspect-ratio: 1; border-radius: var(--radius-md); background: var(--color-panel-raised); }
</style>
