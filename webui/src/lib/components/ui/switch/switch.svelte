<script lang="ts">
  import type { HTMLInputAttributes } from "svelte/elements";

  let {
    checked = $bindable(false),
    class: className = "",
    ...rest
  }: Omit<HTMLInputAttributes, "type" | "role"> & { checked?: boolean } = $props();
</script>

<input {...rest} class={`ui-switch ${className}`} type="checkbox" role="switch" bind:checked />
