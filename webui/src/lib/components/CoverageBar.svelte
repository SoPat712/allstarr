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
</script>

<span
  class:compact
  class="coverage-bar"
  aria-label={`${width(total - unresolved)} percent playable`}
>
  <span class="sr-only">{routes
    .filter((route) => route.providerId !== "unresolved")
    .map((route) => `${providerName(route.providerId)}: ${route.count}`)
    .concat(unresolved ? [`Unresolved: ${unresolved}`] : [])
    .join(", ")}</span>
  {#each routes.filter((route) => route.providerId !== "unresolved") as route}
    <span
      style={`width:${routeWidth(route.count)}%;--route-color:${providerColor(route.providerId)}`}
    ></span>
  {/each}
  {#if unresolved}
    <span
      style={`width:${width(unresolved)}%;--route-color:${providerColor("unresolved")}`}
    ></span>
  {/if}
</span>
