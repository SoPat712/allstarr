<script lang="ts">
  import MappingView from "./MappingView.svelte";
  import ArtistLinksView from "./ArtistLinksView.svelte";

  // Artist links are an administrator's tool; listeners only review their songs.
  let { view = "", administrator = false, initialSearch = "", initialReview = "", initialArtist = "" }:
    { view?: string; administrator?: boolean; initialSearch?: string; initialReview?: string; initialArtist?: string } = $props();
</script>

{#if administrator && view === "artists"}<ArtistLinksView {initialSearch} {initialArtist} />
{:else}<MappingView {initialSearch} {initialReview} {administrator} />{/if}
