<script lang="ts">
  import type { Snippet } from "svelte";
  import { ChevronDown } from "@lucide/svelte";

  let {
    count,
    summary,
    action,
    members,
    expanded = $bindable(false),
  }: {
    count: number;
    summary: string;
    action?: Snippet;
    members?: Snippet;
    expanded?: boolean;
  } = $props();

  const id = $props.id();
</script>

<section class="grouped-row" class:expanded>
  <div class="group-heading">
    <button type="button" class="group-toggle" aria-expanded={expanded} aria-controls={`${id}-members`} onclick={() => expanded = !expanded}>
      <ChevronDown class="group-chevron" size={18} aria-hidden="true" />
      <span class="group-summary">{summary}</span>
      <span class="group-count">{count}</span>
    </button>
    {#if action}<div class="group-action">{@render action()}</div>{/if}
  </div>
  <div id={`${id}-members`} class="group-members" hidden={!expanded}>{@render members?.()}</div>
</section>

<style>
  .grouped-row { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); background: var(--color-panel); }
  .group-heading { display: flex; min-height: var(--control-lg); align-items: center; gap: var(--space-2); padding-right: var(--space-3); }
  .group-toggle { display: flex; min-width: 0; min-height: var(--control-lg); flex: 1; align-items: center; gap: var(--space-2); border: 0; padding: var(--space-3) var(--space-4); background: transparent; color: var(--color-ink); cursor: pointer; text-align: left; }
  .group-toggle:hover { background: color-mix(in srgb, var(--color-ink) 5%, transparent); }
  .group-toggle :global(.group-chevron) { flex: 0 0 auto; color: var(--color-ink-muted); transform: rotate(-90deg); transition: transform var(--motion-standard); }
  .expanded .group-toggle :global(.group-chevron) { transform: rotate(0); }
  .group-summary { min-width: 0; font-size: var(--text-sm); font-weight: 600; }
  .group-count { flex: 0 0 auto; border-radius: var(--radius-sm); padding: var(--space-1) var(--space-2); background: var(--color-panel-raised); color: var(--color-ink-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; }
  .group-action { display: flex; flex: 0 0 auto; align-items: center; }
  .group-members { border-top: 1px solid var(--color-edge); }
  @media (prefers-reduced-motion: reduce) { .group-toggle :global(.group-chevron) { transition: none; } }
</style>
