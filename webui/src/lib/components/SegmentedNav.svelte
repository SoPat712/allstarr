<script lang="ts">
  import { tick } from "svelte";
  import type { Component } from "svelte";

  type Item = { id: string; label: string; href?: string; count?: number; icon?: Component<any> };

  let {
    items,
    active,
    label,
    class: className = "",
    onchange,
  }: {
    items: readonly Item[];
    active: string;
    label: string;
    class?: string;
    onchange?: (id: string) => void;
  } = $props();
  let tablist = $state<HTMLElement>();

  $effect(() => {
    const selectedId = active;
    if (typeof document === "undefined") return;

    let cancelled = false;
    let frame = 0;
    void tick().then(() => {
      if (cancelled || active !== selectedId) return;
      frame = requestAnimationFrame(() => {
        const selected = tablist?.querySelector<HTMLElement>('[aria-selected="true"]');
        if (!tablist || !selected) return;
        const bounds = tablist.getBoundingClientRect();
        const selectedBounds = selected.getBoundingClientRect();
        const next = selected.nextElementSibling as HTMLElement | null;
        const nextBounds = next?.getBoundingClientRect();
        const end = nextBounds && nextBounds.right - selectedBounds.left <= bounds.width
          ? nextBounds.right
          : selectedBounds.right;
        if (selectedBounds.left < bounds.left)
          tablist.scrollTo({ left: tablist.scrollLeft - (bounds.left - selectedBounds.left) });
        else if (end > bounds.right)
          tablist.scrollTo({ left: tablist.scrollLeft + end - bounds.right });
      });
    });

    return () => {
      cancelled = true;
      if (frame) cancelAnimationFrame(frame);
    };
  });

  function navigate(event: KeyboardEvent) {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const tabs = [...(event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>('[role="tab"]')];
    const current = tabs.indexOf(document.activeElement as HTMLElement);
    if (current < 0) return;
    event.preventDefault();
    const next = event.key === "Home"
      ? 0
      : event.key === "End"
        ? tabs.length - 1
        : (current + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    tabs[next].click();
    tabs[next].focus();
  }
</script>

{#snippet tabContent(item: Item)}
  {@const Icon = item.icon}
  {#if Icon}<Icon class="segmented-tab-icon" size={16} aria-hidden="true" />{/if}
  <span class="segmented-tab-label">{item.label}</span>
  {#if item.count !== undefined}<span class="segmented-tab-count">{item.count}</span>{/if}
{/snippet}

<nav class="segmented-nav" aria-label={label}>
  <div
    bind:this={tablist}
    class={`segmented-tabs ${className}`}
    role="tablist"
    tabindex="-1"
    style={`--tab-count:${items.length}`}
    onkeydown={navigate}
  >
    {#each items as item}
      {#if item.href}
        <a
          href={item.href}
          role="tab"
          aria-selected={active === item.id}
          tabindex={active === item.id ? 0 : -1}
        >{@render tabContent(item)}</a>
      {:else}
        <button
          type="button"
          role="tab"
          aria-selected={active === item.id}
          tabindex={active === item.id ? 0 : -1}
          onclick={() => onchange?.(item.id)}
        >{@render tabContent(item)}</button>
      {/if}
    {/each}
  </div>
</nav>
