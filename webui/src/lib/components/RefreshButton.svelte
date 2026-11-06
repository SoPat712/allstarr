<script lang="ts">
  import { RefreshCw } from "@lucide/svelte";
  import { Button, type ButtonProps } from "$lib/components/ui/button";
  import { createManualRefresh } from "$lib/live-updates.svelte";

  let { onrefresh, label = "Refresh", ref = $bindable(null), ...props }: Omit<ButtonProps, "onclick" | "children" | "variant" | "size"> & {
    onrefresh: () => void | Promise<unknown>;
    label?: string;
  } = $props();
  const manual = createManualRefresh();
</script>

<Button {...props} bind:ref variant="text" size="icon" title={manual.pending ? "Refreshing…" : props.title ?? label} aria-label={manual.pending ? "Refreshing…" : label} disabled={props.disabled || manual.pending} aria-busy={manual.pending} onclick={() => void manual.run(onrefresh)}>
  <RefreshCw size={16} class={manual.pending ? "refresh-spinning" : ""} aria-hidden="true" />
</Button>
