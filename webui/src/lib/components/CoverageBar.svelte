<script lang="ts">
  import { providerColor } from "$lib/playlists";

  let {
    routes,
    total,
    unresolved = 0,
    compact = false,
    providerName,
  }: {
    routes: Array<{ providerId: string; count: number }>;
    total: number;
    unresolved?: number;
    compact?: boolean;
    providerName: (providerId: string) => string;
  } = $props();

  const width = (count: number) => total ? Math.round((count / total) * 100) : 0;
  const playable = () => Math.max(0, total - unresolved);
  const routed = () => routes
    .filter((route) => route.providerId !== "unresolved")
    .reduce((sum, route) => sum + route.count, 0);
  const routeWidth = (count: number) =>
    width(count * Math.min(1, playable() / Math.max(1, routed())));
  const songs = (count: number) => `${count} ${count === 1 ? "song" : "songs"}`;
  let tip = $state<{ label: string; x: number; y: number } | null>(null);
  function point(event: PointerEvent) {
    const label = (event.target as HTMLElement).dataset.label;
    const bar = (event.currentTarget as HTMLElement).getBoundingClientRect();
    tip = label ? { label, x: event.clientX, y: bar.top + bar.height - 4 } : null;
  }
</script>

<span
  class:compact
  class="coverage-bar"
  role="img"
  aria-label={`${width(total - unresolved)} percent playable. ${routes
    .filter((route) => route.providerId !== "unresolved")
    .map((route) => `${providerName(route.providerId)}: ${songs(route.count)}`)
    .concat(unresolved ? [`Unresolved: ${songs(unresolved)}`] : []).join(", ")}`}
  onpointermove={point}
  onpointerleave={() => tip = null}
>
  <span class="sr-only">{routes
    .filter((route) => route.providerId !== "unresolved")
    .map((route) => `${providerName(route.providerId)}: ${route.count}`)
    .concat(unresolved ? [`Unresolved: ${unresolved}`] : [])
    .join(", ")}</span>
  {#each routes.filter((route) => route.providerId !== "unresolved") as route}
    {@const label = `${providerName(route.providerId)} · ${songs(route.count)}`}
    <span
      style={`width:${routeWidth(route.count)}%;--route-color:${providerColor(route.providerId)}`}
      data-label={label}
    ></span>
  {/each}
  {#if unresolved}
    <span
      style={`width:${width(unresolved)}%;--route-color:${providerColor("unresolved")}`}
      data-label={`Unresolved · ${songs(unresolved)}`}
    ></span>
  {/if}
</span>
{#if tip}<span class="coverage-tip" aria-hidden="true" style={`left:${tip.x}px;top:${tip.y}px`}>{tip.label}</span>{/if}

<style>
  /* A taller invisible hover area keeps the thin strip easy to point at. */
  .coverage-bar { --strip: .28rem; }
  .coverage-bar.compact { --strip: .42rem; }
  :global(.playlist-row) > .coverage-bar {
    z-index: calc(var(--layer-content) + 1);
    height: .9rem;
    background: linear-gradient(color-mix(in srgb, var(--color-ink) 10%, transparent) 0 0) bottom / 100% var(--strip) no-repeat;
  }
  :global(.playlist-row) > .coverage-bar > span { background: linear-gradient(var(--route-color) 0 0) bottom / 100% var(--strip) no-repeat; }
  .coverage-tip {
    position: fixed; z-index: 130; transform: translate(-50%, calc(-100% - .75rem)); pointer-events: none; white-space: nowrap;
    padding: .375rem .625rem; border-radius: .5rem; background: var(--md-sys-color-inverse-surface);
    color: var(--md-sys-color-inverse-on-surface); font-size: var(--text-xs); font-weight: 600; box-shadow: 0 4px 12px #0003;
  }
</style>
