<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    icon,
    title,
    description,
    action,
  }: {
    icon?: Snippet;
    title: string;
    description?: string;
    action?: Snippet;
  } = $props();
</script>

<div class="empty-state">
  {#if icon}<div class="empty-icon" aria-hidden="true">{@render icon()}</div>{/if}
  <h3>{title}</h3>
  {#if description}<p>{description}</p>{/if}
  {#if action}<div class="empty-action">{@render action()}</div>{/if}
</div>

<style>
  .empty-state { display: flex; min-height: 0; flex-direction: column; align-items: flex-start; justify-content: center; border: 1px dashed var(--color-edge-strong); border-radius: var(--radius-list); background: var(--color-panel); gap: var(--space-2); padding: var(--space-4); text-align: left; }
  .empty-icon { display: grid; width: var(--control-lg); height: var(--control-lg); place-items: center; margin-bottom: var(--space-2); border-radius: var(--radius-lg); background: var(--color-panel-raised); color: var(--color-ink-muted); }
  h3 { margin: 0; color: var(--color-ink); font-size: var(--text-md); font-weight: 600; }
  p { max-width: 36rem; margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  .empty-action { display: flex; width: fit-content; min-width: 0; max-width: 100%; flex-wrap: wrap; justify-content: flex-start; gap: var(--space-2); margin-top: var(--space-3); }
  @media (max-width: 899px) { .empty-action :global([data-slot="button"]) { max-width: 100%; height: auto; min-height: var(--control-md); white-space: normal; overflow-wrap: anywhere; } }
</style>
