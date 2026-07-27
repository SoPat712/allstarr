<script lang="ts">
  type Item = { id: string; label: string; href: string };

  let {
    items,
    active,
    label,
    class: className = "",
  }: {
    items: readonly Item[];
    active: string;
    label: string;
    class?: string;
  } = $props();

  const activeIndex = $derived(Math.max(0, items.findIndex((item) => item.id === active)));

  function navigate(event: KeyboardEvent) {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const tabs = [...(event.currentTarget as HTMLElement).querySelectorAll<HTMLAnchorElement>('[role="tab"]')];
    const current = tabs.indexOf(document.activeElement as HTMLAnchorElement);
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
    class={`segmented-tabs ${className}`}
    role="tablist"
    tabindex="-1"
    style={`--tab-count:${items.length};--tab-index:${activeIndex}`}
    onkeydown={navigate}
  >
    <span class="segmented-tab-indicator" aria-hidden="true"></span>
    {#each items as item}
      <a
        href={item.href}
        role="tab"
        aria-selected={active === item.id}
        tabindex={active === item.id ? 0 : -1}
      >{item.label}</a>
    {/each}
  </div>
</nav>
