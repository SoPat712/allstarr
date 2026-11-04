<script lang="ts">
  import { AlertCircle, RotateCw } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";

  let {
    title,
    detail,
    onretry,
  }: {
    title: string;
    detail?: string;
    onretry?: () => void | Promise<void>;
  } = $props();

  let retrying = $state(false);

  async function retry() {
    if (!onretry || retrying) return;
    retrying = true;
    try {
      await onretry();
    } finally {
      retrying = false;
    }
  }
</script>

<div class="error-state" role="alert">
  <AlertCircle class="error-icon" size={24} aria-hidden="true" />
  <div class="error-copy"><h3>{title}</h3>{#if detail}<p>{detail}</p>{/if}</div>
  {#if onretry}<Button variant="outline" disabled={retrying} onclick={() => void retry()}><RotateCw size={16} aria-hidden="true" />{retrying ? "Trying again…" : "Try again"}</Button>{/if}
</div>

<style>
  .error-state { display: flex; align-items: center; gap: var(--space-4); border: 1px solid color-mix(in srgb, var(--danger) 30%, var(--color-edge)); border-radius: var(--radius-lg); padding: var(--space-5); background: color-mix(in srgb, var(--danger) 5%, var(--color-panel)); }
  .error-state :global(.error-icon) { flex: 0 0 auto; color: var(--danger); }
  .error-copy { min-width: 0; flex: 1; }
  h3 { margin: 0; color: var(--color-ink); font-size: var(--text-sm); font-weight: 600; }
  p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; overflow-wrap: anywhere; }
  @media (max-width: 599px) { .error-state { flex-wrap: wrap; } .error-copy { flex-basis: calc(100% - var(--control-md)); } }
</style>
