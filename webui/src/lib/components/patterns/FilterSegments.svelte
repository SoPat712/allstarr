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
  let wrapped = $state(false);
  let focused = $state<string>();
  const focusId = $derived(focused && options.some(option => option.id === focused) ? focused : options.find(option => option.id === value)?.id ?? options[0]?.id);

  onMount(() => {
    const saved = readFilter(storageKey, options.map(option => option.id));
    if (saved !== undefined && saved !== value) select(saved);
    else if (!options.some(option => option.id === value) && options[0]) select(options[0].id);
    const measure = () => {
      const controls = [...group.querySelectorAll<HTMLButtonElement>('[role="radio"]')];
      wrapped = new Set(controls.map(control => control.offsetTop)).size > 1;
    };
    const observer = new ResizeObserver(measure);
    observer.observe(group);
    measure();
    return () => observer.disconnect();
  });

  // A filter row that scrolls instead of wrapping keeps the chosen filter in view, also after counts arrive.
  $effect(() => {
    void value; void options.map(option => `${option.label}${option.count}`);
    const frame = requestAnimationFrame(() => {
      const checked = group?.querySelector<HTMLElement>('[aria-checked="true"]');
      if (!checked || group.scrollWidth <= group.clientWidth) return;
      const box = group.getBoundingClientRect(), item = checked.getBoundingClientRect();
      if (item.right > box.right) group.scrollLeft += item.right - box.right;
      else if (item.left < box.left) group.scrollLeft -= box.left - item.left;
    });
    return () => cancelAnimationFrame(frame);
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

<div bind:this={group} class="seg filter-segments" class:wrapped role="radiogroup" tabindex="-1" aria-label={label} onkeydown={navigate} onfocusout={(event) => { if (!(event.relatedTarget instanceof Node) || !group.contains(event.relatedTarget)) focused = undefined; }}>
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
      {#if option.count !== undefined}<span class="segment-count">{option.count.toLocaleString()}</span>{/if}
    </button>
  {/each}
</div>

<style>
  .filter-segments { flex-wrap: wrap; overflow: hidden; }
  .filter-segments.wrapped { border-radius: var(--radius-list); }
</style>
