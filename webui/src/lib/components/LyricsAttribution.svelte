<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { lyricsAttribution } from "$lib/lyrics-attribution";
  import type { LyricsAttribution } from "$lib/api";

  let { sourceId, attribution }: { sourceId: string | null; attribution?: LyricsAttribution | null } = $props();
  const credits = $derived(lyricsAttribution(sourceId, attribution));
</script>

{#if credits}
  <section class="settings-impact break-words" aria-label="Lyrics credits">
    <p>Provided by <a href="https://spicylyrics.org/" target="_blank" rel="noopener noreferrer">SpicyLyrics</a>. Lyrics source: {credits.catalog}.</p>
    {#if credits.contributors.length}
      <div class="panel-heading-actions" style="display: flex; flex-direction: column; align-items: start; justify-content: start">
        {#each credits.contributors as contributor}
          {#if contributor.url}
            <Button variant="link" href={contributor.url} target="_blank" rel="noopener noreferrer"
              style="height: auto; white-space: normal; overflow-wrap: anywhere; text-align: start; justify-content: start">{contributor.role} {contributor.name}</Button>
          {:else}
            <span style="max-width: 100%; overflow-wrap: anywhere">{contributor.role} {contributor.name}</span>
          {/if}
        {/each}
      </div>
    {/if}
  </section>
{/if}
