<script lang="ts">
  import { Check, ChevronDown } from "@lucide/svelte";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";

  let {
    label,
    options,
    value = $bindable(""),
    disabled = false,
    onclick,
    onselect,
  }: {
    label: string;
    options: readonly { id: string; label: string; disabled?: boolean }[];
    value?: string;
    disabled?: boolean;
    onclick?: () => void;
    onselect?: (value: string) => void;
  } = $props();

  const scopeLabel = $derived(options.find(option => option.id === value)?.label ?? "Choose scope");

  function select(id: string) {
    value = id;
    onselect?.(id);
  }
</script>

<div class="split-button">
  <Button class="split-primary" {disabled} {onclick}>{label}</Button>
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class={`${buttonVariants()} split-trigger`} disabled={disabled || options.length === 0} aria-label={`${label}: ${scopeLabel}; choose scope`}><span>{scopeLabel}</span><ChevronDown size={16} aria-hidden="true" /></DropdownMenu.Trigger>
    <DropdownMenu.Portal>
      <DropdownMenu.Content preventScroll={false} class="bits-menu split-menu" sideOffset={6} align="end">
        <DropdownMenu.RadioGroup {value} onValueChange={select}>
          {#each options as option (option.id)}
            <DropdownMenu.RadioItem class="bits-menu-item split-option" value={option.id} disabled={option.disabled}>
              <span class="option-indicator" aria-hidden="true">{#if value === option.id}<Check size={16} />{/if}</span>
              <span>{option.label}</span>
            </DropdownMenu.RadioItem>
          {/each}
        </DropdownMenu.RadioGroup>
      </DropdownMenu.Content>
    </DropdownMenu.Portal>
  </DropdownMenu.Root>
</div>

<style>
  .split-button { display: inline-flex; width: fit-content; max-width: 100%; align-items: stretch; }
  .split-button :global(.split-primary) { min-width: 0; min-height: 44px; height: auto; flex-shrink: 1; white-space: normal; border-top-right-radius: 0; border-bottom-right-radius: 0; }
  .split-button :global(.split-trigger) { min-width: 0; min-height: 44px; height: auto; flex-shrink: 1; white-space: normal; border-top-left-radius: 0; border-bottom-left-radius: 0; border-left: 1px solid color-mix(in srgb, var(--color-on-signal) 35%, transparent); }
  :global(.split-menu .split-option) { min-height: var(--control-md); gap: var(--space-2); }
  .option-indicator { display: inline-flex; width: var(--space-4); flex: 0 0 auto; align-items: center; justify-content: center; color: var(--color-signal-text); }
</style>
