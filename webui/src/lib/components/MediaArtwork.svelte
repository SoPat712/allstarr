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
  const source = $derived(url ? adminUrl(url) : "");

  $effect(() => {
    url;
    source;
    failed = false;
  });
</script>

<span class={`media-art ${className}`}>
  {#if url && !failed}
    <img src={source} alt="" {loading} onerror={() => { failed = true; }} />
  {:else}
    <span aria-hidden="true">{fallback}</span>
  {/if}
</span>
