<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  let { added, allowed, busy = false, onadd }: { added: boolean; allowed: boolean; busy?: boolean; onadd: () => void } = $props();
</script>
<div class="app-state">
  {#if added}
    <span class="added">✓ In your apps</span><a href="#/library/playlists">Manage in Playlists</a>
  {:else}
    <Button variant="secondary" disabled={busy || !allowed} aria-busy={busy} onclick={onadd}>Add</Button>
  {/if}
</div>
<style>
  .app-state { display: flex; flex-wrap: wrap; align-items: center; gap: 0 var(--space-2); min-inline-size: 0; min-block-size: var(--control-md); }
  .added { white-space: nowrap; color: var(--color-ink-muted); font-size: var(--text-sm); }
  a { display: inline-flex; align-items: center; min-block-size: var(--control-md); font-size: var(--text-xs); white-space: nowrap; }
</style>
