<script lang="ts" generics="Value extends string">
  import { nextFilterIndex } from "$lib/components/patterns/helpers";

  let { value, label, options, disabled = false, name, onchange }: {
    value: Value;
    label: string;
    options: readonly { value: Value; label: string }[];
    disabled?: boolean;
    name?: string;
    onchange: (value: Value) => void;
  } = $props();

  let buttons: HTMLButtonElement[] = [];
  const focusValue = $derived(options.some(option => option.value === value) ? value : options[0]?.value);

  function choose(next: Value) {
    if (!disabled && next !== value) onchange(next);
  }

  function key(event: KeyboardEvent, current: number) {
    if (disabled) return;
    const next = nextFilterIndex(event.key, current, options.length);
    if (next === undefined) return;
    event.preventDefault();
    buttons[next]?.focus();
    buttons[next]?.scrollIntoView({ block: "nearest", inline: "nearest" });
    choose(options[next].value);
  }
</script>

{#if name}<input type="hidden" {name} {value} />{/if}
<div class="seg use-switch" role="radiogroup" aria-label={label} aria-disabled={disabled}>
  {#each options as item, position (item.value)}
    <button bind:this={buttons[position]} type="button" role="radio" aria-checked={item.value === value}
      tabindex={item.value === focusValue ? 0 : -1} {disabled} onclick={() => choose(item.value)} onkeydown={event => key(event, position)}>
      <span>{item.label}</span>
    </button>
  {/each}
</div>
