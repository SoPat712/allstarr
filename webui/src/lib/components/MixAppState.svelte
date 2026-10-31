<script lang="ts">
  import UiIcon from "$lib/components/UiIcon.svelte";
  import { Button } from "$lib/components/ui/button";
  let { added, allowed, busy = false, onadd }: { added: boolean; allowed: boolean; busy?: boolean; onadd: () => void } = $props();
</script>
<div class="app-state">
  {#if added}
    <span class="added"><UiIcon name="check" size={16} />In your apps</span>
  {:else}
    <Button variant="secondary" disabled={busy || !allowed} aria-busy={busy} onclick={onadd}>Add to my apps</Button>
  {/if}
</div>
<style>
  .app-state { inline-size: calc(var(--space-8) * 5); display: flex; flex-direction: column; align-items: flex-start; justify-content: center; gap: var(--space-1); min-block-size: var(--control-md); }
  .added { min-block-size: var(--space-6); display: inline-flex; align-items: center; gap: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-sm); }
</style>
