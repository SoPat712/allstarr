<script lang="ts">
  import type { MixCard } from "$lib/api";
  import { mixDescription, mixName, shortReadiness } from "$lib/for-you";
  import { Button } from "$lib/components/ui/button";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import MixCover from "$lib/components/MixCover.svelte";
  let { title, cards, onopen, seeAll = "", seeAllLabel = "See all", additions = [], albumArt = false, busy = "", onadd, onaddall, columns = 8 }: {
    title: string; cards: MixCard[]; onopen: (card: MixCard) => void; seeAll?: string; seeAllLabel?: string; additions?: string[]; albumArt?: boolean; busy?: string;
    onadd?: (card: MixCard) => void; onaddall?: () => void; columns?: number;
  } = $props();
  const available = $derived(cards.filter(card => !card.inApps && card.appsAllowed && card.readiness.ready));
</script>
<section aria-label={title}>
  <header><h2>{title}</h2><div class="shelf-actions">
    {#if onaddall && available.length}<Button variant="text" size="sm" disabled={Boolean(busy)} onclick={onaddall}>Add all to apps</Button>{/if}
    {#if seeAll}<a href={seeAll}>{seeAllLabel}</a>{/if}
  </div></header>
  <div class="mix-shelf" style={`--shelf-columns: ${Math.max(1, Math.floor(columns))}`}>
    {#each cards as card, index (`${index}:${card.id}`)}
      <div class="mix-card" role="group" aria-label={mixName(card.name)}>
        <div class="cover">
          <button type="button" class="mix-open" aria-label={`${additions.includes(card.id) ? "Add" : "Open"} ${mixName(card.name)}`} disabled={additions.includes(card.id) && (Boolean(busy) || !card.appsAllowed)} onclick={() => onopen(card)}>
            <span class="artwork" class:unready={!card.readiness.ready}>{#if albumArt}<MixArtwork references={card.coverArtwork} />{:else}<MixCover mix={card} />{/if}</span>
            <strong>{mixName(card.name)}</strong><span class="description">{card.readiness.ready ? mixDescription(card.description, card.kind) : shortReadiness(card.readiness.message)}</span>
          </button>
          {#if onadd && !card.inApps && card.appsAllowed && card.readiness.ready}
            <Button variant="outline" size="sm" class="add-chip" aria-label={`Add ${mixName(card.name)}`} disabled={Boolean(busy)} onclick={() => onadd?.(card)}>{busy === card.id ? "Adding…" : "Add to apps"}</Button>
          {:else if !albumArt}<small class:added={card.inApps}>{card.inApps ? card.section === "yourServices" ? "✓ In your apps · kept in sync" : "✓ In your apps" : ""}</small>{/if}
        </div>
      </div>
    {/each}
  </div>
</section>
<style>
  section { min-width: 0; }
  header, .shelf-actions { display: flex; justify-content: space-between; align-items: center; gap: var(--space-2); }
  header { margin-bottom: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  a { display: inline-flex; min-height: 44px; align-items: center; font-size: var(--text-sm); white-space: nowrap; }
  .mix-shelf { display: grid; grid-template-columns: repeat(var(--shelf-columns), minmax(0, 1fr)); align-items: stretch; gap: var(--space-3); padding-block: var(--space-1) var(--space-2); }
  .mix-card { min-width: 0; text-align: left; color: var(--color-ink); }
  .cover, .mix-open { display: flex; flex-direction: column; width: 100%; min-width: 0; gap: var(--space-2); }
  .cover { height: 100%; }
  .mix-open { flex: 1; text-align: left; color: inherit; border: 0; border-radius: var(--radius-md); background: transparent; cursor: pointer; }
  .mix-open:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 3px; }
  .artwork { display: block; width: 100%; }
  .artwork.unready { opacity: 0.6; }
  small { display: flex; align-items: center; min-height: 44px; margin-top: auto; font-size: var(--text-xs); color: var(--color-ink-muted); }
  small.added { color: var(--success); }
  .cover :global(.add-chip) { align-self: start; min-height: 44px; max-width: 100%; margin-top: auto; padding-inline: var(--space-2); font-size: var(--text-xs); }
  strong { width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--text-sm); font-weight: 600; }
  .description { display: -webkit-box; min-height: 2.8em; overflow: hidden; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.4; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; }
  @media (max-width: 899px) { .mix-shelf { grid-template-columns: none; grid-auto-flow: column; grid-auto-columns: 148px; overflow-x: auto; scroll-snap-type: x mandatory; } .mix-card { scroll-snap-align: start; } }
  @media (pointer: coarse) { .mix-shelf { scrollbar-width: none; } }
</style>
