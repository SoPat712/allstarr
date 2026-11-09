<script lang="ts">
  import type { MixCard, MixSong } from "$lib/api";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import { coverFor } from "$lib/covers/cover-for";
  let { mix, songs }: { mix: MixCard; songs?: MixSong[] } = $props();
  // The server picks the cover's songs (four from different albums), so the page shows what apps show.
  const references = $derived(mix.coverArtwork.length || !songs ? mix.coverArtwork : songs.slice(0, 1).map(item => item.song.id));
</script>
<MixArtwork {references} gradient={coverFor(mix)} />
