<script lang="ts">
  import ExtensionsView from "$lib/components/ExtensionsView.svelte";
  import RoutingView from "$lib/components/RoutingView.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SourcesView from "$lib/components/SourcesView.svelte";

  let {
    section = "services",
    administrator,
    initialSource = "",
    initialSection = "data",
  }: {
    section?: string;
    administrator: boolean;
    initialSource?: string;
    initialSection?: string;
  } = $props();

  const tabs = [
    { id: "services", label: "Services", href: "#/integrations/services" },
    { id: "accounts", label: "Accounts", href: "#/integrations/accounts" },
    { id: "extensions", label: "Extensions", href: "#/integrations/extensions" },
    { id: "routing", label: "Routing", href: "#/integrations/routing" },
  ] as const;

  const active = $derived(tabs.some((item) => item.id === section) ? section : "services");
</script>

<section class="settings-workspace integrations-workspace">
  <SegmentedNav items={tabs} {active} label="Integration sections" class="settings-tabs" />

  {#if active === "services" || active === "accounts"}
    <SourcesView
      mode={active}
      {administrator}
      {initialSource}
      {initialSection}
    />
  {:else if active === "extensions"}
    <ExtensionsView />
  {:else}
    <RoutingView />
  {/if}
</section>
