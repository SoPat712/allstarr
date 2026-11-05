<script lang="ts">
  import type { MixCard, MixSong } from "$lib/api";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import { coverFor } from "$lib/covers/cover-for";
  let { mix, songs }: { mix: MixCard; songs?: MixSong[] } = $props();
  const references = $derived(songs ? songs.map(item => item.song.id) : mix.coverArtwork);
</script>
<MixArtwork {references} gradient={coverFor(mix, songs)} />
