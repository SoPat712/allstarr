<script lang="ts" generics="Value extends string">
  import { Check } from "@lucide/svelte";

  let { value, label, options, widths = [100 / 3, 100 / 3, 100 / 3], disabled = false, compact = false, name, onchange }: {
    value: Value;
    label: string;
    options: readonly { value: Value; label: string; tone?: "primary" | "secondary" | "neutral" }[];
    widths?: readonly [number, number, number];
    disabled?: boolean;
    compact?: boolean;
    name?: string;
    onchange: (value: Value) => void;
  } = $props();

  let buttons: HTMLButtonElement[] = [];
  let group = $state<HTMLDivElement>();
  let thumbLeft = $state<number>();
  let thumbWidth = $state<number>();
  const selected = $derived(Math.max(0, options.findIndex(option => option.value === value)));

  $effect(() => {
    if (!compact || !group) return;
    const container = group;
    const active = buttons[selected];
    if (!active) return;
    const measure = () => {
      const bounds = active.getBoundingClientRect();
      thumbLeft = bounds.left - container.getBoundingClientRect().left;
      thumbWidth = bounds.width;
    };
    const observer = new ResizeObserver(measure);
    observer.observe(container);
    buttons.forEach(button => observer.observe(button));
    measure();
    return () => observer.disconnect();
  });

  function choose(next: Value) {
    if (!disabled && next !== value) onchange(next);
  }

  function key(event: KeyboardEvent, current: number) {
    const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = (current + step + options.length) % options.length;
    buttons[next]?.focus();
    choose(options[next].value);
  }
</script>

{#if name}<input type="hidden" {name} {value} />{/if}
<div bind:this={group} class="use-switch" class:compact role="radiogroup" aria-label={label} aria-disabled={disabled}
  data-tone={options[selected]?.tone ?? "secondary"}
  style:--width-first={widths[0] / 100} style:--width-middle={widths[1] / 100} style:--width-last={widths[2] / 100}
  style:--thumb-left={compact && thumbLeft !== undefined ? `${thumbLeft}px` : undefined}
  style:--thumb-width={compact && thumbWidth !== undefined ? `${thumbWidth}px` : undefined}
  style:--thumb-start={selected === 0 ? "0" : selected === 1 ? "var(--first)" : "calc(var(--first) + var(--middle))"}
  style:--thumb-size={`var(--${["first", "middle", "last"][selected]})`}>
  <span class="use-switch-thumb" aria-hidden="true"></span>
  {#each options as item, position (item.value)}
    <button bind:this={buttons[position]} type="button" role="radio" aria-checked={item.value === value}
      style:padding-inline={compact ? "var(--space-1)" : undefined} style:gap={compact ? "var(--space-1)" : undefined}
      tabindex={item.value === value ? 0 : -1} {disabled} onclick={() => choose(item.value)} onkeydown={event => key(event, position)}>
      {#if item.value === value}<Check size={16} strokeWidth={2.5} aria-hidden="true" />{/if}
      <span style:white-space={compact ? "normal" : undefined}>{item.label}</span>
    </button>
  {/each}
</div>

<style>
  .use-switch {
    position: relative;
    display: grid;
    --inner: calc(100% - 0.5rem);
    --first: var(--width-first);
    --middle: var(--width-middle);
    --last: var(--width-last);
    grid-template-columns: calc(var(--first) * 100%) calc(var(--middle) * 100%) calc(var(--last) * 100%);
    width: 23rem;
    max-width: 100%;
    height: var(--control-md);
    flex: none;
    border-radius: 999px;
    background: var(--color-surface-raised);
    padding: 0 0.25rem;
    box-shadow: inset 0 0 0 1px var(--color-edge);
  }

  .use-switch-thumb {
    position: absolute;
    top: 0.25rem;
    bottom: 0.25rem;
    left: var(--thumb-left, calc(0.25rem + var(--inner) * var(--thumb-start, 0)));
    width: var(--thumb-width, calc(var(--inner) * var(--thumb-size)));
    border-radius: 999px;
    background: var(--md-sys-color-secondary-container);
    box-shadow: 0 1px 2px rgb(0 0 0 / 0.18);
    transition: left 240ms cubic-bezier(0.2, 0, 0, 1), width 240ms cubic-bezier(0.2, 0, 0, 1), background-color 240ms;
  }

  .use-switch[data-tone="primary"] .use-switch-thumb {
    background: var(--md-sys-color-primary-container);
  }

  .use-switch[data-tone="neutral"] .use-switch-thumb {
    background: var(--md-sys-color-surface-container-highest, var(--color-panel-raised));
    box-shadow: 0 1px 2px rgb(0 0 0 / 0.18), inset 0 0 0 1px var(--color-edge-strong);
  }

  button {
    position: relative;
    z-index: 1;
    display: inline-flex;
    min-width: 0;
    min-height: var(--control-md);
    align-items: center;
    justify-content: center;
    gap: 0.375rem;
    border: 0;
    border-radius: 999px;
    background: transparent;
    padding: 0 var(--space-2);
    color: var(--color-ink-muted);
    font: inherit;
    font-size: var(--text-sm);
    font-weight: 500;
    line-height: 1.25rem;
    white-space: nowrap;
    cursor: pointer;
    transition: color 160ms;
  }

  button span {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .compact {
    display: flex;
  }

  .compact button {
    flex: 1 1 auto;
    min-width: min-content;
  }

  .compact button span {
    min-width: min-content;
  }

  .compact button :global(svg) {
    flex-shrink: 0;
  }

  button[aria-checked="true"] {
    color: var(--md-sys-color-on-secondary-container);
    font-weight: 600;
  }

  .use-switch[data-tone="primary"] button[aria-checked="true"] {
    color: var(--md-sys-color-on-primary-container);
  }

  .use-switch[data-tone="neutral"] button[aria-checked="true"] {
    color: var(--color-ink);
  }

  button:not([aria-checked="true"]):not(:disabled):hover {
    color: var(--color-ink);
  }

  button:focus-visible {
    outline: 2px solid var(--color-ring);
    outline-offset: 2px;
  }

  button:disabled {
    cursor: not-allowed;
  }

  .use-switch[aria-disabled="true"] {
    opacity: 0.55;
  }

  @media (max-width: 40rem) {
    .use-switch {
      width: 100%;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .use-switch-thumb,
    button {
      transition: none;
    }
  }
</style>
