<script lang="ts" module>
  // Fingerprints are shared across rows and pages so revisiting a page does not refetch artwork.
  const fingerprints = new Map<string, Promise<bigint>>();
</script>

<script lang="ts">
  import { adminUrl } from "$lib/admin-url";
  import { differenceHash, hashSimilarity, percent } from "$lib/mappings";

  let { source, candidate }: { source: string; candidate: string } = $props();
  let similarity = $state<number | null>(null);
  let visible = $state(false);
  let anchor = $state<HTMLElement>();

  $effect(() => {
    if (!anchor || visible) return;
    if (typeof IntersectionObserver === "undefined") { visible = true; return; }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) { visible = true; observer.disconnect(); }
    }, { rootMargin: "200px" });
    observer.observe(anchor);
    return () => observer.disconnect();
  });

  $effect(() => {
    if (!visible) return;
    let current = true;
    similarity = null;
    Promise.all([fingerprint(source), fingerprint(candidate)])
      .then(([left, right]) => {
        if (current) similarity = hashSimilarity(left, right);
      })
      .catch(() => {});
    return () => { current = false; };
  });

  function fingerprint(url: string) {
    let pending = fingerprints.get(url);
    if (!pending) {
      pending = compute(url);
      pending.catch(() => fingerprints.delete(url));
      fingerprints.set(url, pending);
    }
    return pending;
  }

  async function compute(url: string) {
    const response = await fetch(adminUrl(url));
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
{:else}
  <span bind:this={anchor} aria-hidden="true"></span>
{/if}
