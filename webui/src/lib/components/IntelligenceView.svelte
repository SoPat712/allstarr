<script lang="ts">
  import { onMount } from "svelte";
  import { audioMuse, type AudioMuseStatus } from "$lib/api";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import IntelligenceForYou from "$lib/components/IntelligenceForYou.svelte";
  import IntelligenceReleases from "$lib/components/IntelligenceReleases.svelte";
  import IntelligenceStats from "$lib/components/IntelligenceStats.svelte";
  import IntelligenceSounds from "$lib/components/IntelligenceSounds.svelte";
  import IntelligenceTaste from "$lib/components/IntelligenceTaste.svelte";

  let { initialSection = "for-you", administrator = false }: { initialSection?: string; administrator?: boolean } = $props();
  let status = $state<AudioMuseStatus | null>(null), loaded = $state(false);
  let pending: Promise<void> | null = null, disposed = false;
  const soundsVisible = $derived(administrator || status?.reason === null);
  const sections = $derived([
    { id: "for-you", label: "For you", href: "#/intelligence?section=for-you" },
    ...(soundsVisible ? [{ id: "sounds", label: "Sounds", href: "#/intelligence?section=sounds" }] : []),
    { id: "releases", label: "New releases", href: "#/intelligence?section=releases" },
    { id: "taste", label: "Your taste", href: "#/intelligence?section=taste" },
    { id: "stats", label: "Your stats", href: "#/intelligence?section=stats" },
  ]);
  const active = $derived(sections.some(item => item.id === initialSection) ? initialSection : "for-you");
  async function loadStatus() {
    if (disposed) return;
    if (pending) return pending;
    pending = (async () => {
      try { const next = await audioMuse.status(); if (!disposed) status = next; }
      catch { if (!disposed) status = { reason: "not_reachable", accessibleLibrarySongs: 0, analyzedSongs: 0, coverageComplete: false }; }
      finally { if (!disposed) loaded = true; pending = null; }
    })();
    return pending;
  }
  async function refreshStatus() { await pending; await loadStatus(); }
  const scheduler = createRefreshScheduler(loadStatus);
  onMount(() => {
    void loadStatus();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

<SegmentedNav label="Intelligence sections" items={sections} {active} />
<div class="intelligence-content">
  {#if initialSection === "sounds" && !loaded && !administrator}<Skeleton class="skeleton-panel" aria-label="Loading sounds" />
  {:else if active === "sounds"}<IntelligenceSounds {administrator} {status} onrefresh={refreshStatus} />
  {:else if active === "taste"}<IntelligenceTaste />
  {:else if active === "releases"}<IntelligenceReleases />
  {:else if active === "stats"}<IntelligenceStats />
  {:else}<IntelligenceForYou />{/if}
</div>

<style>
  .intelligence-content { min-width: 0; margin-top: var(--section-gap); }
</style>
