<script lang="ts">
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
</section>
