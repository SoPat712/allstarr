<script lang="ts">
  import type { Snippet } from "svelte";
  import { ChevronRight } from "@lucide/svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import "$lib/components/ui/button/touch-target.css";

  let {
    title,
    subtitle,
    leading,
    chips,
    pill,
    trailing,
    menu,
    onclick,
    actionLabel,
    href,
    selected = false,
    attention = false,
    selection = false,
    checked = $bindable(false),
    oncheck,
  }: {
    title: string;
    subtitle?: string;
    leading?: Snippet;
    chips?: Snippet;
    pill?: Snippet;
    trailing?: Snippet;
    menu?: Snippet;
    onclick?: (event: MouseEvent) => void;
    actionLabel?: string;
    href?: string;
    selected?: boolean;
    attention?: boolean;
    selection?: boolean;
    checked?: boolean;
    oncheck?: (checked: boolean) => void;
  } = $props();

  const id = $props.id();
  const clickable = $derived(Boolean(onclick || href));

  function stopControlEvent(event: Event) {
    event.stopPropagation();
  }
</script>

<div class="item-row" class:selected class:needs-review={attention} class:clickable data-selected={selected || undefined}>
  {#if href}
    <a class="row-action" {href} aria-label={actionLabel} aria-labelledby={actionLabel ? undefined : `${id}-title`} aria-describedby={subtitle ? `${id}-subtitle` : undefined} aria-current={selected ? "true" : undefined} {onclick}></a>
  {:else if onclick}
    <button class="row-action" type="button" aria-label={actionLabel} aria-labelledby={actionLabel ? undefined : `${id}-title`} aria-describedby={subtitle ? `${id}-subtitle` : undefined} aria-pressed={selected} {onclick}></button>
  {/if}
  {#if selection}
    <div class="row-selection row-control" onclick={stopControlEvent} onkeydown={stopControlEvent} role="presentation">
      <Checkbox bind:checked aria-label={`Select ${title}`} onCheckedChange={(next) => oncheck?.(next)} />
    </div>
  {/if}
  {#if leading}<div class="row-leading">{@render leading()}</div>{/if}
  <div class="row-copy">
    <h3 id={`${id}-title`}>{title}</h3>
    {#if subtitle}<p id={`${id}-subtitle`}>{subtitle}</p>{/if}
    {#if chips}<div class="row-chips">{@render chips()}</div>{/if}
  </div>
  {#if pill}<div class="row-pill">{@render pill()}</div>{/if}
  {#if trailing || menu}
    <div class="row-controls row-control touch-targets" onclick={stopControlEvent} onkeydown={stopControlEvent} role="presentation">
      {@render trailing?.()}
      {@render menu?.()}
    </div>
  {/if}
  {#if clickable}<ChevronRight class="row-chevron" size={18} aria-hidden="true" />{/if}
</div>

<style>
  .item-row { position: relative; display: flex; min-width: 0; min-height: 56px; align-items: center; gap: var(--space-3); isolation: isolate; border-bottom: 1px solid var(--color-edge); padding: var(--space-2) var(--space-4); background: var(--color-panel); }
  .item-row:last-child { border-bottom: 0; }
  .item-row.selected { background: color-mix(in srgb, var(--color-signal) 9%, var(--color-panel)); }
  .item-row.needs-review { box-shadow: inset 3px 0 var(--warning); }
  .item-row.clickable:has(.row-action:hover) { background: color-mix(in srgb, var(--color-ink) 5%, var(--color-panel)); }
  .row-action { position: absolute; z-index: 0; inset: 0; border: 0; border-radius: inherit; background: transparent; cursor: pointer; }
  .row-action:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -3px; }
  .row-leading, .row-copy, .row-pill, .item-row :global(.row-chevron) { position: relative; z-index: 1; pointer-events: none; }
  .row-leading { display: grid; width: 36px; height: 36px; flex: 0 0 auto; place-items: center; overflow: hidden; border-radius: var(--radius-sm); }
  .row-leading :global(.media-art) { width: 100%; height: 100%; }
  .row-leading :global(img) { width: 100%; height: 100%; object-fit: cover; }
  .row-copy { min-width: 0; flex: 1; }
  h3 { overflow: hidden; margin: 0; color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; line-height: 1.4; text-overflow: ellipsis; white-space: nowrap; }
  p { overflow: hidden; margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.45; text-overflow: ellipsis; white-space: nowrap; }
  .row-chips { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-1); margin-top: var(--space-2); }
  .row-control { position: relative; z-index: 2; }
  .row-selection { display: flex; width: var(--control-md); min-height: var(--control-md); flex: 0 0 auto; align-items: center; justify-content: center; }
  .row-controls { display: flex; flex: 0 0 auto; align-items: center; gap: var(--space-1); }
  .row-pill { flex: 0 0 auto; }
  .item-row :global(.row-chevron) { flex: 0 0 auto; color: var(--color-ink-muted); }
  @media (max-width: 899px) {
    .item-row { min-height: 64px; flex-wrap: wrap; gap: var(--space-2); padding-inline: var(--space-3); }
    .row-leading { width: 40px; height: 40px; }
    .row-pill { min-width: 0; max-width: 35%; }
    .row-pill :global(.badge), .row-pill :global(.state-pill) { max-width: 100%; white-space: normal; overflow-wrap: anywhere; }
    .row-controls { min-width: 0; max-width: 100%; flex-shrink: 1; flex-wrap: wrap; }
    .row-controls :global([data-slot="button"]) { max-width: 100%; height: auto; flex-shrink: 1; white-space: normal; overflow-wrap: anywhere; }
  }
</style>
