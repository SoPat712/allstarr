<script lang="ts">
  import { LoaderCircle, X } from "@lucide/svelte";
  import { toast, toasts, type Toast } from "$lib/toasts";
  let running = $state<number | null>(null);
  let height = $state(0);
  $effect(() => {
    const root = document.documentElement;
    root.style.setProperty("--toast-height", `${height}px`);
    return () => root.style.removeProperty("--toast-height");
  });
  function notificationLayer(node: HTMLElement) {
    // Keep controls in the active focus scope; the top layer avoids panel clipping.
    const sync = () => {
      const dialogs = document.querySelectorAll('[role="dialog"][data-state="open"], [role="alertdialog"][data-state="open"]');
      const target = dialogs.item(dialogs.length - 1) ?? document.body;
      if (node.parentElement !== target) target.append(node);
      if (!node.matches(":popover-open")) node.showPopover();
    };
    const observer = new MutationObserver(sync);
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-state"] });
    sync();
    return { destroy: () => { observer.disconnect(); node.remove(); } };
  }
  async function act(item: Toast) {
    if (!item.action || running !== null) return;
    running = item.id;
    try { await item.action.run(); toast.dismiss(item.id); }
    catch { toast.error("That action could not be completed. Try again."); }
    finally { running = null; }
  }
</script>

<section class="toaster" aria-label="Notifications" popover="manual" use:notificationLayer bind:offsetHeight={height}>
  {#each $toasts as item (item.id)}
    <div class="toast" class:toast-error={item.kind === "error"}
      role={item.kind === "error" ? "alert" : "status"}
      aria-live={item.kind === "error" ? "assertive" : "polite"} aria-atomic="true"
      onmouseenter={() => toast.pause(item.id, "hover")} onmouseleave={() => toast.resume(item.id, "hover")}
      onfocusin={() => toast.pause(item.id, "focus")}
      onfocusout={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) toast.resume(item.id, "focus"); }}
      onkeydowncapture={(event) => { if (event.key === "Escape") { event.stopPropagation(); toast.dismiss(item.id); } }}>
      {#if item.busy}<LoaderCircle class="toast-busy" size={18} aria-hidden="true" />{/if}<p>{item.message}</p>
      {#if item.action}<button type="button" class="toast-action" disabled={running !== null} onclick={() => void act(item)}>{item.action.label}</button>{/if}
      <button type="button" class="toast-dismiss" aria-label="Dismiss notification" onclick={() => toast.dismiss(item.id)}><X size={18} aria-hidden="true" /></button>
    </div>
  {/each}
</section>

<style>
  :global(:root) { --toast-bottom: max(24px, calc(var(--side-panel-footer-height, 0px) + var(--space-3))); }
  .toaster { position: fixed; z-index: 120; inset: auto 24px var(--toast-bottom) auto; width: min(420px, calc(100vw - 48px)); margin: 0; padding: 0; border: 0; overflow: visible; background: transparent; color: inherit; display: grid; gap: 8px; pointer-events: none; }
  .toast { pointer-events: auto; display: flex; align-items: center; gap: 8px; padding: 8px 8px 8px 16px; border-radius: 12px; background: var(--md-sys-color-inverse-surface); color: var(--md-sys-color-inverse-on-surface); box-shadow: 0 4px 12px #0003; animation: toast-enter 180ms ease-out; }
  .toast p { margin: 0; flex: 1; min-width: 0; overflow-wrap: anywhere; font-size: var(--text-sm); line-height: 1.45; }
  .toast button { flex: none; min-width: 44px; min-height: 44px; border: 0; border-radius: 8px; color: inherit; background: transparent; cursor: pointer; }
  .toast button:focus-visible { outline: 2px solid currentColor; outline-offset: -2px; }
  .toast-action { color: var(--md-sys-color-inverse-on-surface) !important; font-weight: 650; padding: 0 8px; }
  .toast :global(.toast-busy) { flex: none; animation: signal-boot-spin 1s linear infinite; }
  .toast-error { border-left: 4px solid var(--md-sys-color-error); }
  @keyframes toast-enter { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: translateY(0); } }
  @media (max-width: 767px) {
    :global(:root) { --toast-bottom: max(calc(var(--mobile-nav-height, 72px) + 16px), calc(var(--side-panel-footer-height, 0px) + var(--space-3))); }
    .toaster { left: 16px; right: 16px; width: auto; }
  }
  @media (max-width: 599px) { :global(body:has(.try-dialog)) .toaster { top: max(var(--space-2), env(safe-area-inset-top)); bottom: auto; } }
  @media (prefers-reduced-motion: reduce) { .toast, .toast :global(.toast-busy) { animation: none; } }
</style>
