<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    children,
    tone = "neutral",
    class: className = "",
  }: {
    children: Snippet;
    tone?: "neutral" | "accent" | "success" | "warning" | "danger";
    class?: string;
  } = $props();
</script>

<span class={`badge badge-${tone} ${className}`.trim()}>{@render children()}</span>
