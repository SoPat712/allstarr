<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    tone = "neutral",
    text,
    icon,
    children,
  }: {
    tone?: "ok" | "warn" | "stop" | "neutral" | "info";
    text?: string;
    icon?: Snippet;
    children?: Snippet;
  } = $props();
</script>

<span class="state-pill" data-tone={tone}>
  {#if icon}<span class="pill-icon" aria-hidden="true">{@render icon()}</span>{/if}
  {#if text}{text}{/if}{@render children?.()}
</span>

<style>
  .state-pill { --pill-color: var(--color-ink-muted); display: inline-flex; width: fit-content; align-items: center; gap: var(--space-1); border-radius: var(--radius-sm); padding: var(--space-1) var(--space-2); background: color-mix(in srgb, var(--pill-color) 11%, transparent); color: var(--pill-color); font-size: var(--text-xs); font-weight: 600; line-height: 1.35; white-space: nowrap; }
  [data-tone="ok"] { --pill-color: var(--success); }
  [data-tone="warn"] { --pill-color: var(--warning); }
  [data-tone="stop"] { --pill-color: var(--danger); }
  [data-tone="info"] { --pill-color: var(--info); }
  .pill-icon { display: inline-flex; align-items: center; }
</style>
