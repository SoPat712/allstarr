<script lang="ts">
  import RoutingView from "$lib/components/RoutingView.svelte";
  import SourcesView from "$lib/components/SourcesView.svelte";

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
    <nav class="route-tabs flex flex-wrap gap-2" aria-label="Integration sections"><a class="button secondary" href="#/integrations/services">Services</a><a class="button secondary" href="#/integrations/accounts">Accounts</a><a class="button" href="#/integrations/routing" aria-current="page">Routing</a></nav>
    <RoutingView />
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
  {#if administrator}<a class="button secondary m-4" href="#/integrations/routing">Routing and quality</a>{/if}
  {/if}
</section>
