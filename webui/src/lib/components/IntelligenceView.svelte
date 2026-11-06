<script lang="ts">
  import type { Component } from "svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  let { initialSection = "for-you", initialMix = "", initialRelease = "", administrator = false, listenerId = "", listenerName = "", initialImport = false }: { initialImport?: boolean; initialSection?: string; initialMix?: string; initialRelease?: string; administrator?: boolean; listenerId?: string; listenerName?: string } = $props();
  const sections = [
    { id: "for-you", label: "For you", href: "#/for-you" },
    { id: "releases", label: "New releases", href: "#/for-you?section=releases" },
    { id: "taste", label: "Your taste", href: "#/for-you?section=taste" },
    { id: "recap", label: "Recap", href: "#/for-you?section=recap" },
  ];
  const active = $derived(initialSection === "stats" ? "recap" : sections.some(item => item.id === initialSection) ? initialSection : "for-you");
  let View = $state<Component<any>>(), MixPanel = $state<Component<any>>();
  let loaded = $state(""), error = $state("");
  let request = 0;
  const sectionImports: Record<string, () => Promise<{ default: Component<any> }>> = {
    "for-you": () => import("$lib/components/IntelligenceForYou.svelte"),
    releases: () => import("$lib/components/IntelligenceReleases.svelte"),
    taste: () => import("$lib/components/IntelligenceTaste.svelte"),
    recap: () => import("$lib/components/IntelligenceStats.svelte"),
  };
  const views = new Map<string, Promise<{ default: Component<any> }>>();
  function sectionLoader(section: string) {
    const cached = views.get(section);
    if (cached) return cached;
    const pending = (sectionImports[section] ?? sectionImports["for-you"])();
    views.set(section, pending);
    void pending.catch(() => views.delete(section));
    return pending;
  }
  async function load(section: string) {
    const version = ++request;
    error = "";
    try {
      const module = await sectionLoader(section);
      if (version === request) { View = module.default; loaded = section; }
    } catch { if (version === request) error = "This page could not be loaded. Try again."; }
  }
  $effect(() => { void load(active); });
  async function loadMixPanel() {
    try { MixPanel = (await import("$lib/components/MixDetailPanel.svelte")).default; }
    catch { error = "The mix panel could not be loaded. Try again."; }
  }
  $effect(() => { if (initialMix && !MixPanel) void loadMixPanel(); });
</script>
<SegmentedNav variant="tabs" label="For you sections" items={sections} {active} />
<div class="intelligence-content" class:panel-open={Boolean(initialMix)}>
  {#if error}<ErrorState title="Couldn't load this page" detail={error} onretry={async () => { await load(active); if (initialMix && !MixPanel) await loadMixPanel(); }} />{/if}
  {#if View && loaded === active}<View {administrator} {listenerId} {listenerName} {initialImport} {initialRelease} />{:else if !error}<RowsSkeleton />{/if}
</div>
{#if initialMix && MixPanel}<MixPanel id={initialMix} {administrator} />{/if}
<style>
  .intelligence-content { min-width: 0; margin-top: var(--section-gap); }
</style>
