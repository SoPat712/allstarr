<script lang="ts">
  import type { MixCard } from "$lib/api";
  import { mixDescription, mixName, shortReadiness } from "$lib/for-you";
  import { Button } from "$lib/components/ui/button";
  import MixCover from "$lib/components/MixCover.svelte";
  let { title, cards, onopen, seeAll = "", onseeall, expanded = false, seeAllLabel = "See all", additions = [], albumArt = false, busy = "", onadd, onremove, onaddall, columns = 8 }: {
    title: string; cards: MixCard[]; onopen: (card: MixCard) => void; seeAll?: string; onseeall?: () => void; expanded?: boolean; seeAllLabel?: string; additions?: string[]; albumArt?: boolean; busy?: string;
    onadd?: (card: MixCard) => void; onremove?: (card: MixCard) => void; onaddall?: () => void; columns?: number;
  } = $props();
  const available = $derived(cards.filter(card => !card.inApps && card.appsAllowed && card.readiness.ready));
</script>
<section aria-label={title}>
  <header class:expandable={Boolean(onseeall)}><h2>{title}</h2><div class="shelf-actions">
    {#if onaddall && available.length}<Button variant="text" size="sm" disabled={Boolean(busy)} onclick={onaddall}>Add all to apps</Button>{/if}
    {#if onseeall}<Button variant="text" aria-expanded={expanded} onclick={onseeall}>{expanded ? "Show less" : seeAllLabel}</Button>
    {:else if seeAll}<Button variant="text" size="sm" href={seeAll}>{seeAllLabel}</Button>{/if}
  </div></header>
  <div class="mix-shelf" class:expanded style={`--shelf-columns: ${Math.max(1, Math.floor(columns))}`}>
    {#each cards as card, index (`${index}:${card.id}`)}
      <div class="mix-card" role="group" aria-label={mixName(card.name)}>
        <div class="cover">
          <button type="button" class="mix-open" aria-label={`${additions.includes(card.id) ? "Add" : "Open"} ${mixName(card.name)}`} disabled={additions.includes(card.id) && (Boolean(busy) || !card.appsAllowed)} onclick={() => onopen(card)}>
            <span class="artwork" class:unready={!card.readiness.ready}><MixCover mix={card} /></span>
            <strong>{mixName(card.name)}</strong><span class="description">{card.readiness.ready || card.section === "yourServices" ? mixDescription(card.description, card.kind) : shortReadiness(card.readiness.message)}</span>
          </button>
          {#if card.savedFrom}<span class="description saved-from">Saved from {card.savedFrom}</span>{/if}
          {#if onadd && !card.inApps && card.appsAllowed && card.readiness.ready}
            <Button variant="outline" size="sm" class="add-chip" aria-label={`Add ${mixName(card.name)}`} disabled={Boolean(busy)} onclick={() => onadd?.(card)}>{busy === card.id ? "Adding…" : "Add to apps"}</Button>
          {:else if onremove && card.inApps}
            <Button variant="outline" size="sm" class="add-chip" aria-label={`Remove ${mixName(card.name)} from apps`} disabled={Boolean(busy)} onclick={() => onremove?.(card)}>{busy === card.id ? "Removing…" : "Remove from apps"}</Button>
          {:else if !albumArt}<small class:added={card.inApps}>{card.inApps ? card.section === "yourServices" ? "✓ In your apps · kept in sync" : "✓ In your apps" : ""}</small>{/if}
        </div>
      </div>
    {/each}
  </div>
</section>
<style>
  section { container-type: inline-size; min-width: 0; }
  header, .shelf-actions { display: flex; justify-content: space-between; align-items: center; gap: var(--space-2); }
  header { margin-bottom: var(--space-3); }
  header.expandable { flex-wrap: wrap; }
  h2 { font-size: 1.125rem; }
  .mix-shelf { display: grid; grid-auto-flow: column; grid-auto-columns: calc((100% - (var(--shelf-columns) - 1) * var(--space-3)) / var(--shelf-columns)); overflow-x: auto; scroll-snap-type: x mandatory; align-items: stretch; gap: var(--space-3); padding-block: var(--space-1) var(--space-2); }
  .mix-shelf.expanded { grid-auto-flow: row; grid-template-columns: repeat(var(--shelf-columns), minmax(0, 1fr)); overflow: visible; }
  .mix-card { min-width: 0; scroll-snap-align: start; text-align: left; color: var(--color-ink); }
  .cover, .mix-open { display: flex; flex-direction: column; width: 100%; min-width: 0; gap: var(--space-2); }
  .cover { height: 100%; }
  .mix-open { flex: 1; text-align: left; color: inherit; border: 0; border-radius: var(--radius-md); background: transparent; cursor: pointer; }
  .mix-open:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 3px; }
  .artwork { display: block; width: 100%; }
  .artwork.unready { opacity: 0.6; }
  small { display: flex; align-items: center; min-height: var(--control-sm); height: var(--control-sm); margin-top: auto; font-size: var(--text-xs); color: var(--color-ink-muted); }
  small.added { color: var(--success); }
  .cover :global(.add-chip) { align-self: stretch; width: 100%; max-width: 100%; margin-top: auto; }
  strong { display: -webkit-box; width: 100%; overflow: hidden; white-space: normal; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; font-size: var(--text-sm); font-weight: 600; }
  .description { display: -webkit-box; min-height: 2.8em; overflow: hidden; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.4; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; }
  .saved-from { min-height: 0; }
  @media (min-width: 900px) and (max-width: 1199px) { .mix-shelf { grid-auto-columns: 132px; } }
  @container (max-width: 899px) { .mix-shelf { grid-auto-columns: 132px; } }
  @media (max-width: 899px) { .mix-shelf { grid-auto-columns: 148px; } }
  @media (max-width: 899px), (pointer: coarse) { small { min-height: 44px; height: 44px; } }
  @container (max-width: 899px) { .mix-shelf.expanded { grid-template-columns: repeat(auto-fill, minmax(132px, 1fr)); } }
  @media (pointer: coarse) { .mix-shelf { scrollbar-width: none; } }
</style>
