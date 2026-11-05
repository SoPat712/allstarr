<script lang="ts">
  import { adminUrl } from "$lib/admin-url";
  import { untrack } from "svelte";
  import { imageHasLoaded, reconcileImageSources, type ImageLoadState } from "$lib/image-loading";

  let {
    url,
    fallback = "♪",
    loading = "lazy",
    class: className = "",
  }: {
    url?: string | null;
    fallback?: string;
    loading?: "eager" | "lazy";
    class?: string;
  } = $props();

  let image = $state<HTMLImageElement>();
  let loadState = $state<ImageLoadState>(reconcileImageSources(undefined, []));
  const source = $derived(url ? adminUrl(url) : "");

  $effect(() => {
    const next = reconcileImageSources(untrack(() => loadState), source ? [source] : [], untrack(() => image));
    if (next !== untrack(() => loadState)) loadState = next;
  });
  $effect(() => {
    if (imageHasLoaded(image, source)) loadState.loaded = true;
  });
</script>

<span class={`media-art ${className}`}>
  {#if url && !loadState.failed}
    <img bind:this={image} src={source} alt="" {loading} onload={() => { loadState.loaded = true; }} onerror={() => { loadState.failed = true; }} />
  {/if}
  {#if !url || loadState.failed || !loadState.loaded}
    <span aria-hidden="true">{fallback}</span>
  {/if}
</span>

<style>
  .media-art > img, .media-art > span { grid-area: 1 / 1; }
</style>
