<script lang="ts">
  import type { ProviderUseMode } from "$lib/api";
  import { Check } from "@lucide/svelte";

  let { value, label, disabled = false, onchange }: {
    value: ProviderUseMode; label: string; disabled?: boolean; onchange: (mode: ProviderUseMode) => void;
  } = $props();

  const positions: { mode: ProviderUseMode; text: string }[] = [
    { mode: "automatic", text: "On" },
    { mode: "manualOnly", text: "Manual only" },
    { mode: "off", text: "Off" },
  ];
  let buttons: HTMLButtonElement[] = [];

  function choose(mode: ProviderUseMode) {
    if (!disabled && mode !== value) onchange(mode);
  }
  function key(event: KeyboardEvent, current: number) {
    const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = (current + step + positions.length) % positions.length;
    buttons[next]?.focus();
    choose(positions[next].mode);
  }
</script>

<div class="use-switch" role="radiogroup" aria-label={label} aria-disabled={disabled} data-mode={value}>
  <span class="use-switch-thumb" aria-hidden="true"></span>
  {#each positions as item, position (item.mode)}
    <button bind:this={buttons[position]} type="button" role="radio" aria-checked={item.mode === value}
      tabindex={item.mode === value ? 0 : -1} {disabled} onclick={() => choose(item.mode)} onkeydown={event => key(event, position)}>
      {#if item.mode === value}<Check size={16} strokeWidth={2.5} aria-hidden="true" />{/if}
      <span>{item.text}</span>
    </button>
  {/each}
</div>

<style>
  .use-switch {
    position: relative;
    display: grid;
    /* Segments are sized to their labels; the thumb slides and resizes between the three detents. */
    --inner: calc(100% - 0.5rem);
    grid-template-columns: 27.5% 45% 27.5%;
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
    left: calc(0.25rem + var(--inner) * var(--thumb-start, 0));
    width: calc(var(--inner) * var(--thumb-size, 0.275));
    border-radius: 999px;
    background: var(--md-sys-color-secondary-container);
    box-shadow: 0 1px 2px rgb(0 0 0 / 0.18);
    transition: left 240ms cubic-bezier(0.2, 0, 0, 1), width 240ms cubic-bezier(0.2, 0, 0, 1), background-color 240ms;
  }

  .use-switch[data-mode="manualOnly"] {
    --thumb-start: 0.275;
    --thumb-size: 0.45;
  }

  .use-switch[data-mode="off"] {
    --thumb-start: 0.725;
  }

  .use-switch[data-mode="automatic"] .use-switch-thumb {
    background: var(--md-sys-color-primary-container);
  }

  .use-switch[data-mode="off"] .use-switch-thumb {
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

  button[aria-checked="true"] {
    color: var(--md-sys-color-on-secondary-container);
    font-weight: 600;
  }

  .use-switch[data-mode="automatic"] button[aria-checked="true"] {
    color: var(--md-sys-color-on-primary-container);
  }

  .use-switch[data-mode="off"] button[aria-checked="true"] {
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
