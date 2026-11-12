<script lang="ts" module>
  // Switching swaps the whole view, so the new switch takes focus back when the old one had it.
  let focusAfterSwitch = false;
</script>

<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import SegmentedNav from "./SegmentedNav.svelte";

  let { active }: { active: "songs" | "artists" } = $props();
  let root = $state<HTMLElement>();

  function change(id: string) {
    if (id === active) return;
    focusAfterSwitch = root?.contains(document.activeElement) ?? false;
    void goto(id === "artists" ? "#/library/matches?view=artists" : "#/library/matches", { keepFocus: true, noScroll: true });
  }

  onMount(() => {
    if (!focusAfterSwitch) return;
    focusAfterSwitch = false;
    root?.querySelector<HTMLElement>('[aria-selected="true"]')?.focus();
  });
</script>

<span class="match-kind-switch" bind:this={root}>
  <SegmentedNav variant="segments" class="match-kind" label="Match songs or artists" {active} onchange={change} items={[
    { id: "songs", label: "Songs" },
    { id: "artists", label: "Artists" },
  ]} />
</span>

<style>
  .match-kind-switch { display: contents; }
</style>
