<script lang="ts">
  import type { Snippet } from "svelte";
  import { CircleAlert, Layers, ChevronDown } from "@lucide/svelte";

  let {
    count,
    summary,
    reason,
    tone = "warn",
    icon,
    action,
    members,
    expanded = $bindable(false),
    collapsible = true,
  }: {
    count: number;
    summary: string;
    reason?: string;
    tone?: "warn" | "neutral";
    icon?: Snippet;
    action?: Snippet;
    members?: Snippet;
    expanded?: boolean;
    collapsible?: boolean;
  } = $props();

  const id = $props.id();
</script>

<section class="grouped-row" class:expanded data-tone={tone} class:static-group={!collapsible} aria-label={collapsible ? undefined : summary}>
  <div class="group-heading">
    {#if collapsible}
    <button type="button" class="group-toggle" aria-expanded={expanded} aria-controls={`${id}-members`} onclick={() => expanded = !expanded}>
      <span class="group-icon" aria-hidden="true">{#if icon}{@render icon()}{:else if tone === "warn"}<CircleAlert size={18} />{:else}<Layers size={18} />{/if}</span>
      <span class="group-copy"><span class="group-summary"><span class="group-count">{count.toLocaleString()}</span> {summary}</span>{#if reason}<span class="group-reason">{reason}</span>{/if}</span>
      <ChevronDown class="group-chevron" size={16} aria-hidden="true" />
    </button>
    {:else}<h3 class="group-label">{summary} · {count}</h3>{/if}
    {#if action}<div class="group-action">{@render action()}</div>{/if}
  </div>
  <div id={`${id}-members`} class="group-members" hidden={collapsible && !expanded}>{@render members?.()}</div>
</section>

<style>
  .grouped-row { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-list); background: var(--color-panel); }
  .grouped-row[data-tone="warn"] { border-left: 3px solid var(--warning); }
  .group-heading { position: relative; display: flex; min-height: 58px; align-items: center; gap: var(--space-3); padding-right: calc(var(--space-3) + 28px); }
  .group-toggle { display: flex; min-width: 0; min-height: 58px; flex: 1; align-items: center; gap: var(--space-3); border: 0; padding: var(--space-3) var(--space-4); background: transparent; color: var(--color-ink); cursor: pointer; text-align: left; }
  .group-toggle:hover { background: color-mix(in srgb, var(--color-ink) 5%, transparent); }
  .group-icon { display: grid; width: 36px; height: 36px; flex: 0 0 auto; place-items: center; border-radius: 50%; background: var(--warning-container); color: var(--warning); }
  [data-tone="neutral"] .group-icon { background: var(--color-panel-raised); color: var(--color-ink-muted); }
  .group-copy { display: grid; min-width: 0; gap: var(--space-1); }
  .group-summary { font-size: var(--text-sm); font-weight: 600; }
  .group-count { font-variant-numeric: tabular-nums; }
  .group-reason { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.4; overflow-wrap: anywhere; }
  .group-toggle :global(.group-chevron) { position: absolute; top: 50%; right: var(--space-3); color: var(--color-ink-muted); transform: translateY(-50%); transition: transform var(--motion-standard); }
  .expanded .group-toggle :global(.group-chevron) { transform: translateY(-50%) rotate(180deg); }
  .group-action { display: flex; min-width: 0; flex: 0 1 auto; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
  .group-members { border-top: 1px solid var(--color-edge); }
  @media (max-width: 599px) {
    .group-heading { flex-wrap: wrap; gap: 0 var(--space-2); padding-right: 0; }
    .group-toggle { flex-basis: 100%; padding-right: 40px; }
    .group-toggle :global(.group-chevron) { top: 30px; }
    .group-action { order: 2; flex-basis: 100%; padding: 0 var(--space-4) var(--space-3); }
  }
  .static-group[data-tone] { border: 0; border-radius: 0; }
  .static-group + :global(.static-group) { border-top: 1px solid var(--color-edge); }
  .static-group .group-heading { min-height: 0; padding: 0; background: var(--color-panel-raised); }
  .group-label { margin: 0; padding: 10px 14px; color: var(--color-ink-muted); font-size: .7rem; font-weight: 500; letter-spacing: .06em; text-transform: uppercase; }
  @media (prefers-reduced-motion: reduce) { .group-toggle :global(.group-chevron) { transition: none; } }
</style>
