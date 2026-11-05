<script lang="ts">
  import { countLabel } from "$lib/count-label";
  let {
    bars,
    latency,
    label = "API latency",
  }: { bars: number; latency?: number | null; label?: string } = $props();
  const description = $derived(
    `${bars} of 4 connectivity bars${latency == null ? "" : `, ${countLabel(Math.round(latency), "millisecond")} ${label.toLowerCase()}`}`,
  );
</script>

<span class="connectivity-bars" role="img" aria-label={description}>
  <span>{#each [1, 2, 3, 4] as bar}<i class:active={bar <= bars}></i>{/each}</span>
  {#if latency != null}<small>{Math.round(latency)} ms</small>{/if}
</span>
