<script lang="ts">
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import IntelligenceForYou from "$lib/components/IntelligenceForYou.svelte";
  import IntelligenceReleases from "$lib/components/IntelligenceReleases.svelte";
  import IntelligenceStats from "$lib/components/IntelligenceStats.svelte";

  let { initialSection = "for-you" }: { initialSection?: string } = $props();
  const sections = [
    { id: "for-you", label: "For you", href: "#/intelligence?section=for-you" },
    { id: "releases", label: "New releases", href: "#/intelligence?section=releases" },
    { id: "stats", label: "Your stats", href: "#/intelligence?section=stats" },
  ];
  const active = $derived(sections.some(item => item.id === initialSection) ? initialSection : "for-you");
</script>

<SegmentedNav label="Intelligence sections" items={sections} {active} />
<div class="intelligence-content">
  {#if active === "releases"}<IntelligenceReleases />
  {:else if active === "stats"}<IntelligenceStats />
  {:else}<IntelligenceForYou />{/if}
</div>

<style>
  .intelligence-content { min-width: 0; margin-top: var(--section-gap); }
</style>
