<script lang="ts">
  import { differenceHash, hashSimilarity, percent } from "$lib/mappings";

  let { source, candidate }: { source: string; candidate: string } = $props();
  let similarity = $state<number | null>(null);

  $effect(() => {
    let current = true;
    similarity = null;
    Promise.all([fingerprint(source), fingerprint(candidate)])
      .then(([left, right]) => {
        if (current) similarity = hashSimilarity(left, right);
      })
      .catch(() => {});
    return () => { current = false; };
  });

  async function fingerprint(url: string) {
    const response = await fetch(url);
    if (!response.ok) throw new Error("Artwork unavailable");
    const bitmap = await createImageBitmap(await response.blob());
    try {
      const canvas = document.createElement("canvas");
      canvas.width = 9;
      canvas.height = 8;
      const context = canvas.getContext("2d", { willReadFrequently: true });
      if (!context) throw new Error("Canvas unavailable");
      context.drawImage(bitmap, 0, 0, 9, 8);
      return differenceHash(context.getImageData(0, 0, 9, 8).data);
    } finally {
      bitmap.close();
    }
  }
</script>

{#if similarity != null}
  <span><small>artwork</small><strong>{percent(similarity)}</strong></span>
{/if}
