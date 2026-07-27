<script lang="ts">
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

  $effect(() => {
    url;
    failed = false;
  });
</script>

<span class={`media-art ${className}`}>
  {#if url && !failed}
    <img src={url} alt="" {loading} onerror={() => { failed = true; }} />
  {:else}
    <span aria-hidden="true">{fallback}</span>
  {/if}
</span>
