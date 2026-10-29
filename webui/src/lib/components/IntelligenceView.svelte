<script lang="ts">
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import IntelligenceForYou from "$lib/components/IntelligenceForYou.svelte";
  import IntelligenceReleases from "$lib/components/IntelligenceReleases.svelte";
  import IntelligenceStats from "$lib/components/IntelligenceStats.svelte";
  import IntelligenceTaste from "$lib/components/IntelligenceTaste.svelte";
  import MixPage from "$lib/components/MixPage.svelte";
  let { initialSection = "for-you", initialMix = "", initialRelease = "", administrator = false, listenerId = "", listenerName = "", initialImport = false }: { initialImport?: boolean; initialSection?: string; initialMix?: string; initialRelease?: string; administrator?: boolean; listenerId?: string; listenerName?: string } = $props();
  const sections = [
    { id: "for-you", label: "For you", href: "#/for-you" },
    { id: "releases", label: "New releases", href: "#/for-you?section=releases" },
    { id: "taste", label: "Your taste", href: "#/for-you?section=taste" },
    { id: "recap", label: "Recap", href: "#/for-you?section=recap" },
  ];
  const active = $derived(initialSection === "stats" ? "recap" : sections.some(item => item.id === initialSection) ? initialSection : "for-you");
</script>
<SegmentedNav label="For you sections" items={sections} {active} />
<div class="intelligence-content">
  {#if active === "taste"}<IntelligenceTaste />
  {:else if active === "releases"}<IntelligenceReleases {initialRelease} />
  {:else if active === "recap"}<IntelligenceStats />
  {:else}<IntelligenceForYou {administrator} {listenerId} {listenerName} {initialImport} />{/if}
</div>
{#if initialMix}<MixPage id={initialMix} {administrator} />{/if}
<style>
  .intelligence-content { min-width: 0; margin-top: var(--section-gap); }
</style>
