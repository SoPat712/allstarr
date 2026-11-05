<script lang="ts">
  import type { Snippet } from "svelte";
  import { MediaQuery } from "svelte/reactivity";
  import { ArrowUp, ArrowDown, X } from "@lucide/svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { isTypingTarget } from "./helpers";

  let {
    open = $bindable(false),
    title,
    description,
    artwork,
    header,
    children,
    footer,
    menu,
    navigationLabel = "song",
    previousLabel,
    nextLabel,
    onPrevious,
    onNext,
    onclose,
  }: {
    open?: boolean;
    title: string;
    description?: string;
    artwork?: Snippet;
    header?: Snippet;
    children?: Snippet;
    footer?: Snippet;
    menu?: Snippet;
    navigationLabel?: string;
    previousLabel?: string;
    nextLabel?: string;
    onPrevious?: () => void;
    onNext?: () => void;
    onclose?: () => void;
  } = $props();

  let navigation: HTMLElement | null = null;
  let navigationWasInert = false;
  function restoreNavigation() {
    if (!navigation) return;
    navigation.inert = navigationWasInert;
    navigation = null;
  }
  const sheet = new MediaQuery("(max-width: 899px)");
  $effect(() => {
    if (!open || !sheet.current) return;
    navigation = document.querySelector<HTMLElement>(".mobile-navigation");
    if (!navigation) return;
    navigationWasInert = navigation.inert;
    navigation.inert = true;
    return restoreNavigation;
  });
  let footerHeight = $state(0);
  $effect(() => {
    if (!open) return;
    const root = document.documentElement;
    root.style.setProperty("--side-panel-footer-height", `${footerHeight}px`);
    return () => root.style.removeProperty("--side-panel-footer-height");
  });

  function navigate(event: KeyboardEvent) {
    if (!open || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || isTypingTarget(event.target)) return;
    const dialog = event.target instanceof Element ? event.target.closest('[role="dialog"], [role="alertdialog"]') : null;
    if (dialog && !dialog.hasAttribute("data-side-panel")) return;
    if (!(onPrevious || onNext) || !["ArrowUp", "ArrowDown"].includes(event.key)) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.key === "ArrowUp") onPrevious?.();
    else onNext?.();
  }
</script>

<svelte:window onkeydowncapture={navigate} />

<Dialog.Root bind:open onOpenChange={(next) => { if (!next) onclose?.(); }}>
  <Dialog.Portal>
    <Dialog.Overlay class="side-panel-overlay" />
    <Dialog.Content
      data-side-panel
      class="side-panel"
      preventScroll={false}
      trapFocus={true}
      onCloseAutoFocus={restoreNavigation}
    >
      <div class="panel-layout">
        <div class="panel-grip" aria-hidden="true"><span></span></div>
        <header class="panel-header" class:with-artwork={Boolean(artwork)}>
          {#if artwork}<div class="panel-artwork">{@render artwork()}</div>{/if}
          <div class="panel-title">
            <Dialog.Title>{title}</Dialog.Title>
            {#if description}<Dialog.Description>{description}</Dialog.Description>{/if}
          </div>
          <div class="panel-controls touch-targets">
            <nav class="panel-navigation" aria-label="Browse items">
              <Button variant="ghost" size="icon" aria-label={previousLabel ?? `Previous ${navigationLabel}`} disabled={!onPrevious} onclick={onPrevious}><ArrowUp size={16} aria-hidden="true" /></Button>
              <Button variant="ghost" size="icon" aria-label={nextLabel ?? `Next ${navigationLabel}`} disabled={!onNext} onclick={onNext}><ArrowDown size={16} aria-hidden="true" /></Button>
            </nav>
            {@render menu?.()}
            <Dialog.Close class={buttonVariants({ variant: "ghost", size: "icon" })} aria-label="Close"><X size={18} aria-hidden="true" /></Dialog.Close>
          </div>
          {#if header}<div class="panel-header-extra">{@render header()}</div>{/if}
        </header>
        <div class="panel-body">{@render children?.()}</div>
        {#if footer}<footer class="panel-footer" bind:offsetHeight={footerHeight}>{@render footer()}</footer>{/if}
      </div>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  :global(.side-panel-overlay) { position: fixed; z-index: var(--layer-overlay); inset: 0; background: var(--overlay); }
  :global(.side-panel) { position: fixed; z-index: var(--layer-dialog); inset: 0 0 0 auto; display: flex; width: 100%; height: 100dvh; flex-direction: column; overflow: clip; border-left: 1px solid var(--color-edge); outline: none; background: var(--color-panel); box-shadow: var(--shadow-lg); }
  .panel-layout { display: flex; min-height: 0; height: 100%; flex: 1 1 auto; flex-direction: column; overflow: clip; }
  :global(.side-panel[data-state="open"]) { animation: panel-in var(--motion-standard); }
  :global(.side-panel[data-state="closed"]) { animation: panel-out var(--motion-standard); }
  .panel-grip { display: flex; height: var(--space-5); flex: 0 0 auto; align-items: center; justify-content: center; }
  .panel-grip span { width: var(--space-8); height: var(--space-1); border-radius: var(--radius-sm); background: var(--color-edge-strong); }
  .panel-header { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-2) var(--space-3); flex: 0 0 auto; border-bottom: 1px solid var(--color-edge); padding: var(--space-3) var(--space-4); }
  .panel-header.with-artwork { grid-template-columns: 56px minmax(0, 1fr) auto; }
  .panel-artwork { width: 56px; height: 56px; overflow: hidden; border-radius: var(--radius-sm); }
  .panel-artwork :global(.media-art) { width: 100%; height: 100%; }
  .panel-title { min-width: 0; }
  .panel-title :global(h2) { overflow: hidden; margin: 0; color: var(--color-ink); font-size: 1.05rem; font-weight: 600; line-height: 1.25; text-overflow: ellipsis; white-space: nowrap; }
  .panel-title :global(p) { overflow: hidden; margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.4; text-overflow: ellipsis; white-space: nowrap; }
  .panel-controls, .panel-navigation { display: flex; align-items: center; gap: 0; }
  .panel-controls :global(button) { display: inline-flex; width: 32px; height: 32px; min-height: var(--touch-target-min, 32px); align-items: center; justify-content: center; padding: 0; color: var(--color-ink-muted); }
  .panel-header-extra { grid-column: 1 / -1; min-width: 0; overflow-wrap: anywhere; color: var(--color-ink-muted); font-size: .8125rem; line-height: 1.4; }
  .panel-body { min-height: 0; flex: 1 1 auto; overflow-y: auto; overscroll-behavior-y: contain; padding: var(--space-4); }
  :global(body:has(.toaster .toast)) .panel-body { margin-bottom: max(0px, calc(var(--toast-height, 0px) + var(--toast-bottom, 24px) - var(--side-panel-footer-height, 0px) + var(--space-3))); }
  .panel-body :global(.sec-label) { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: .7rem; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; }
  .panel-footer { display: flex; flex: 0 0 auto; align-items: center; justify-content: flex-end; border-top: 1px solid var(--color-edge); padding: var(--space-3) var(--space-4) max(var(--space-3), env(safe-area-inset-bottom)); background: var(--color-panel); }
  @media (min-width: 900px) { :global(.side-panel-overlay) { display: none; } :global(.side-panel) { width: min(480px, 40vw); } .panel-grip { display: none; } }
  @keyframes panel-in { from { opacity: 0; transform: translateX(100%); } to { opacity: 1; transform: translateX(0); } }
  @keyframes panel-out { from { opacity: 1; transform: translateX(0); } to { opacity: 0; transform: translateX(100%); } }
  @media (max-width: 899px) {
    .panel-footer > :global(*) { min-width: 0; max-width: 100%; }
    .panel-footer :global([data-slot="button"]) { min-width: 44px; max-width: 100%; height: auto; min-height: 44px; flex-shrink: 1; white-space: normal; overflow-wrap: anywhere; }
    :global(.side-panel-overlay) { z-index: max(var(--layer-overlay), calc(var(--layer-navigation) + 1)); }
    :global(.side-panel) { z-index: max(var(--layer-dialog), calc(var(--layer-navigation) + 2)); inset: 0; height: 100dvh; max-height: 100dvh; border-left: 0; }
    @keyframes panel-in { from { opacity: 0; transform: translateY(100%); } to { opacity: 1; transform: translateY(0); } }
    @keyframes panel-out { from { opacity: 1; transform: translateY(0); } to { opacity: 0; transform: translateY(100%); } }
  }
  @media (prefers-reduced-motion: reduce) { :global(.side-panel[data-state]) { animation: none; } }
</style>
