<script lang="ts">
  import { onMount } from "svelte";
  import { nextFilterIndex, readFilter, writeFilter, type FilterOption } from "./helpers";

  let {
    options,
    value = $bindable(""),
    storageKey,
    label = "Filter items",
    onchange,
  }: {
    options: readonly FilterOption[];
    value?: string;
    storageKey?: string;
    label?: string;
    onchange?: (value: string) => void;
  } = $props();

  let group: HTMLDivElement;
  let focused = $state<string>();
  const focusId = $derived(focused && options.some(option => option.id === focused) ? focused : options.find(option => option.id === value)?.id ?? options[0]?.id);

  onMount(() => {
    const saved = readFilter(storageKey, options.map(option => option.id));
    if (saved !== undefined && saved !== value) select(saved);
    else if (!options.some(option => option.id === value) && options[0]) select(options[0].id);
  });

  function select(id: string) {
    value = id;
    focused = id;
    writeFilter(storageKey, id);
    onchange?.(id);
  }

  function navigate(event: KeyboardEvent) {
    const controls = [...group.querySelectorAll<HTMLButtonElement>('[role="radio"]')];
    const current = controls.indexOf(document.activeElement as HTMLButtonElement);
    if (current < 0) return;
    const next = nextFilterIndex(event.key, current, controls.length);
    if (next === undefined) return;
    event.preventDefault();
    focused = options[next].id;
    controls[next].focus();
    controls[next].scrollIntoView({ block: "nearest", inline: "nearest" });
  }
</script>

<div bind:this={group} class="filter-segments" role="radiogroup" tabindex="-1" aria-label={label} onkeydown={navigate} onfocusout={(event) => { if (!(event.relatedTarget instanceof Node) || !group.contains(event.relatedTarget)) focused = undefined; }}>
  {#each options as option (option.id)}
    <button
      type="button"
      role="radio"
      aria-checked={value === option.id}
      class:zero={option.count === 0}
      tabindex={focusId === option.id ? 0 : -1}
      onclick={() => select(option.id)}
      onfocus={() => focused = option.id}
    >
      <span>{option.label}</span>
      {#if option.count !== undefined}<span class="segment-count">{option.count}</span>{/if}
    </button>
  {/each}
</div>

<style>
  .filter-segments { display: flex; width: fit-content; min-width: 0; max-width: 100%; flex-wrap: wrap; gap: var(--space-1); padding: var(--space-1); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); background: var(--color-panel); }
  button { display: inline-flex; min-height: var(--control-md); min-width: var(--control-md); max-width: 100%; flex: 0 0 auto; align-items: center; justify-content: center; gap: var(--space-2); border: 0; border-radius: var(--radius-md); padding: var(--space-2) var(--space-3); background: transparent; color: var(--color-ink-muted); cursor: pointer; font-size: var(--text-sm); font-weight: 500; white-space: normal; transition: background var(--motion-standard), color var(--motion-standard); }
  button > span { min-width: 0; overflow-wrap: anywhere; }
  button:hover { background: var(--color-panel-raised); color: var(--color-ink); }
  button[aria-checked="true"] { background: var(--color-signal-muted); color: var(--color-signal-text); font-weight: 700; }
  .segment-count { font-variant-numeric: tabular-nums; }
  button.zero { opacity: 0.55; }
  @media (prefers-reduced-motion: reduce) { button { transition: none; } }
</style>
