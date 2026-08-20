<script lang="ts">
  type Item = { id: string; label: string; href?: string; count?: number };

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
    active;
    if (typeof document === "undefined") return;
    queueMicrotask(() => {
      const selected = tablist?.querySelector<HTMLElement>('[aria-selected="true"]');
      if (!tablist || !selected) return;
      const start = selected.offsetLeft;
      const end = start + selected.offsetWidth;
      if (start < tablist.scrollLeft) tablist.scrollTo({ left: start });
      else if (end > tablist.scrollLeft + tablist.clientWidth)
        tablist.scrollTo({ left: end - tablist.clientWidth });
    });
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

<nav aria-label={label}>
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
        >{item.label}{#if item.count !== undefined}<span class="segmented-tab-count">{item.count}</span>{/if}</a>
      {:else}
        <button
          type="button"
          role="tab"
          aria-selected={active === item.id}
          tabindex={active === item.id ? 0 : -1}
          onclick={() => onchange?.(item.id)}
        >{item.label}{#if item.count !== undefined}<span class="segmented-tab-count">{item.count}</span>{/if}</button>
      {/if}
    {/each}
  </div>
</nav>
