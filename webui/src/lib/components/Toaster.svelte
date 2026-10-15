<script lang="ts">
  import { X } from "@lucide/svelte";
  import { toast, toasts, type Toast } from "$lib/toasts";
  let running = $state<number | null>(null);
  async function act(item: Toast) {
    if (!item.action || running !== null) return;
    running = item.id;
    try { await item.action.run(); toast.dismiss(item.id); }
    catch { toast.error("That action could not be completed. Try again."); }
    finally { running = null; }
  }
</script>

<section class="toaster" aria-label="Notifications">
  {#each $toasts as item (item.id)}
    <div class="toast" class:toast-error={item.kind === "error"}
      role={item.kind === "error" ? "alert" : "status"}
      aria-live={item.kind === "error" ? "assertive" : "polite"} aria-atomic="true"
      onmouseenter={() => toast.pause(item.id, "hover")} onmouseleave={() => toast.resume(item.id, "hover")}
      onfocusin={() => toast.pause(item.id, "focus")}
      onfocusout={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) toast.resume(item.id, "focus"); }}
      onkeydown={(event) => { if (event.key === "Escape") { event.stopPropagation(); toast.dismiss(item.id); } }}>
      <p>{item.message}</p>
      {#if item.action}<button type="button" class="toast-action" disabled={running !== null} onclick={() => void act(item)}>{item.action.label}</button>{/if}
      <button type="button" class="toast-dismiss" aria-label="Dismiss notification" onclick={() => toast.dismiss(item.id)}><X size={18} aria-hidden="true" /></button>
    </div>
  {/each}
</section>

<style>
  .toaster { position: fixed; z-index: 120; right: 24px; bottom: 24px; width: min(420px, calc(100vw - 48px)); display: grid; gap: 8px; pointer-events: none; }
  .toast { pointer-events: auto; display: flex; align-items: center; gap: 8px; padding: 8px 8px 8px 16px; border-radius: 12px; background: var(--md-sys-color-inverse-surface); color: var(--md-sys-color-inverse-on-surface); box-shadow: 0 4px 12px #0003; animation: toast-enter 180ms ease-out; }
  .toast p { margin: 0; flex: 1; min-width: 0; overflow-wrap: anywhere; font-size: var(--text-sm); line-height: 1.45; }
  .toast button { flex: none; min-width: 44px; min-height: 44px; border: 0; border-radius: 8px; color: inherit; background: transparent; cursor: pointer; }
  .toast button:focus-visible { outline: 2px solid currentColor; outline-offset: -2px; }
  .toast-action { color: var(--md-sys-color-inverse-on-surface) !important; font-weight: 650; padding: 0 8px; }
  .toast-error { border-left: 4px solid var(--md-sys-color-error); }
  @keyframes toast-enter { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: translateY(0); } }
  @media (max-width: 767px) { .toaster { left: 16px; right: 16px; width: auto; bottom: calc(var(--mobile-nav-height, 72px) + 16px); } }
  @media (prefers-reduced-motion: reduce) { .toast { animation: none; } }
</style>
