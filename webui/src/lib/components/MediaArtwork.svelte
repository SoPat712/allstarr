<script lang="ts">
  import { adminUrl } from "$lib/admin-url";

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

  let failed = $state(false);
  let loaded = $state(false);
  const source = $derived(url ? adminUrl(url) : "");

  $effect(() => {
    url;
    source;
    failed = false;
    loaded = false;
  });
</script>

<span class={`media-art ${className}`}>
  {#if url && !failed}
    <img src={source} alt="" {loading} onload={() => { loaded = true; }} onerror={() => { failed = true; }} />
  {/if}
  {#if !url || failed || !loaded}
    <span aria-hidden="true">{fallback}</span>
  {/if}
</span>

<style>
  .media-art > img, .media-art > span { grid-area: 1 / 1; }
</style>
