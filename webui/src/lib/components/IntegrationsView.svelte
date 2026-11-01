<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RouteError from "$lib/components/RouteError.svelte";
  import SourcesView from "$lib/components/SourcesView.svelte";
  import SharedProviderHearts from "$lib/components/SharedProviderHearts.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";

  let {
    section = "services",
    administrator,
    developmentIntelligence = false,
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
    developmentIntelligence?: boolean;
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
  {#if !serviceId}
    <SegmentedNav
      class="route-tabs"
      label="Source sections"
      active={section === "routing" && administrator ? "routing" : section === "accounts" ? "accounts" : "services"}
      items={[
        { id: "services", label: administrator ? "Services" : "Available services", href: "#/integrations/services" },
        { id: "accounts", label: administrator ? "Accounts" : "My accounts", href: "#/integrations/accounts" },
        ...(administrator ? [{ id: "routing", label: "Routing and quality", href: "#/integrations/routing" }] : []),
      ]}
    />
  {/if}
  {#if section === "routing" && administrator}
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
    {developmentIntelligence}
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
  {/if}
</section>
