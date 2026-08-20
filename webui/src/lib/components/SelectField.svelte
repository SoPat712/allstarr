<script lang="ts">
  import { Select } from "$lib/components/ui/select";
  import { Check, ChevronDown } from "@lucide/svelte";

  type Option = string | { value: string; label: string; disabled?: boolean };

  let {
    value = $bindable(""),
    options,
    name,
    label,
    placeholder = "Select an option",
    required = false,
    disabled = false,
    onchange,
    class: className = "",
  }: {
    value: string;
    options: readonly Option[];
    name?: string;
    label: string;
    placeholder?: string;
    required?: boolean;
    disabled?: boolean;
    onchange?: (value: string) => void;
    class?: string;
  } = $props();

  const items = $derived(options.map((option) => typeof option === "string"
    ? { value: option, label: option }
    : option));
  const visiblePlaceholder = $derived(value === ""
    ? items.find((item) => item.value === "")?.label ?? placeholder
    : placeholder);
</script>

<div class="select-root">
  <Select.Root
    type="single"
    bind:value
    {items}
    {name}
    {required}
    {disabled}
    onValueChange={(next) => onchange?.(next)}
  >
    <Select.Trigger class={`select-trigger ${className}`.trim()} aria-label={label}>
      <Select.Value placeholder={visiblePlaceholder} />
      <ChevronDown class="select-chevron" size={16} aria-hidden="true" />
    </Select.Trigger>
    <Select.Portal>
      <Select.Content class="select-content" sideOffset={6}>
        <Select.Viewport>
          {#each items as item}
            <Select.Item class="select-item" value={item.value} label={item.label} disabled={item.disabled}>
              <span>{item.label}</span><Check class="select-check" size={16} aria-hidden="true" />
            </Select.Item>
          {/each}
        </Select.Viewport>
      </Select.Content>
    </Select.Portal>
  </Select.Root>
</div>
