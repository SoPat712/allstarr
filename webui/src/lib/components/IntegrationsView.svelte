<script lang="ts">
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
      active={section === "accounts" ? "accounts" : "services"}
      items={[
        { id: "services", label: administrator ? "Services" : "Available services", href: "#/sources/services" },
        { id: "accounts", label: administrator ? "Accounts" : "My accounts", href: "#/sources/accounts" },
      ]}
    />
  {/if}
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
</section>
