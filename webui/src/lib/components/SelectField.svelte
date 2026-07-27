<script lang="ts">
  import { Select } from "bits-ui";

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
      <Select.Value {placeholder} />
      <span class="select-chevron" aria-hidden="true">⌄</span>
    </Select.Trigger>
    <Select.Portal>
      <Select.Content class="select-content" sideOffset={6}>
        <Select.Viewport>
          {#each items as item}
            <Select.Item class="select-item" value={item.value} label={item.label} disabled={item.disabled}>
              <span>{item.label}</span><span class="select-check" aria-hidden="true">✓</span>
            </Select.Item>
          {/each}
        </Select.Viewport>
      </Select.Content>
    </Select.Portal>
  </Select.Root>
</div>
