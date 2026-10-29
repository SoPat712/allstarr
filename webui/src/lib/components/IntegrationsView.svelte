<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RouteError from "$lib/components/RouteError.svelte";
  import SourcesView from "$lib/components/SourcesView.svelte";
  import SharedProviderHearts from "$lib/components/SharedProviderHearts.svelte";
  import { Button } from "$lib/components/ui/button";

  let {
    section = "services",
    administrator,
    currentUserId = "",
    initialSource = "",
    initialSection = "data",
    initialConnect = false,
    serviceId = "",
    from = "",
    focusAccount = "",
  }: {
    section?: string;
    administrator: boolean;
    currentUserId?: string;
    initialSource?: string;
    initialSection?: string;
    initialConnect?: boolean;
    serviceId?: string;
    from?: string;
    focusAccount?: string;
  } = $props();

</script>

<section class="settings-workspace integrations-workspace">
  {#if section === "routing" && administrator}
    <nav class="route-tabs flex flex-wrap gap-2" aria-label="Integration sections"><Button variant="secondary" href="#/integrations/services">Services</Button><Button variant="secondary" href="#/integrations/accounts">Accounts</Button><Button href="#/integrations/routing" aria-current="page">Routing</Button></nav>
    {#await import("$lib/components/RoutingView.svelte")}
      <Skeleton class="skeleton-panel" aria-label="Loading routing and quality" />
    {:then { default: RoutingView }}
      <RoutingView />
    {:catch}
      <RouteError eyebrow="Routing unavailable" title="Routing could not be loaded." message="Reload to try again." onRetry={() => window.location.reload()} />
    {/await}
  {:else}
  <SourcesView
    mode={section === "accounts" ? "accounts" : "services"}
    {administrator}
    {currentUserId}
    {initialSource}
    {initialSection}
    {initialConnect}
    {serviceId}
    {from}
    {focusAccount}
  />
  {#if administrator && section === "accounts" && !serviceId && !initialSource && !initialConnect}
    <SharedProviderHearts {administrator} />
  {/if}
  {#if administrator && section !== "accounts" && !serviceId}
    <section class="settings-card p-5 my-4 space-y-3" aria-labelledby="routing-entry-title">
      <h3 id="routing-entry-title" class="font-semibold">Routing and quality</h3>
      <p>Choose which sources are tried first for search and playback, and the audio quality listeners get.</p>
      <Button variant="secondary" href="#/integrations/routing">Open routing and quality</Button>
    </section>
  {/if}
  {/if}
</section>
