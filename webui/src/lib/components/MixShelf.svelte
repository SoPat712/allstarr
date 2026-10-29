<script lang="ts">
  import type { MixCard } from "$lib/api";
  import { mixName, shortReadiness } from "$lib/for-you";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import MixCover from "$lib/components/MixCover.svelte";
  let { title, cards, onopen, seeAll = "", additions = [], albumArt = false }: { title: string; cards: MixCard[]; onopen: (card: MixCard) => void; seeAll?: string; additions?: string[]; albumArt?: boolean } = $props();
</script>
<section>
  <header><h2>{title}</h2>{#if seeAll}<a href={seeAll}>See all</a>{/if}</header>
  <div class="mix-shelf" role="region" aria-label={title}>
    {#each cards as card (card.id)}
      <button type="button" class="mix-card" onclick={() => onopen(card)}>
        <span class="cover">{#if albumArt}<MixArtwork references={card.coverArtwork} />{:else}<MixCover mix={card} />{/if}{#if additions.includes(card.id)}<span class="add-chip">Add</span>{/if}</span>
        <strong>{mixName(card.name)}</strong><span class="description">{card.readiness.ready ? card.description : shortReadiness(card.readiness.message)}</span>
        <small>{card.inApps ? "✓ In your apps" : ""}</small>
      </button>
    {/each}
  </div>
</section>
<style>
  section { min-width: 0; }
  header { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); margin-bottom: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  a { display: inline-flex; min-height: 44px; align-items: center; }
  .mix-shelf { display: grid; grid-auto-flow: column; grid-auto-columns: 160px; overflow-x: auto; scroll-snap-type: x mandatory; gap: 12px; padding-bottom: var(--space-2); }
  .mix-card { scroll-snap-align: start; display: flex; flex-direction: column; gap: var(--space-2); min-width: 0; text-align: left; color: var(--color-ink); cursor: pointer; border-radius: var(--radius-md); }
  .mix-card:focus-visible { outline: 2px solid var(--color-signal); outline-offset: -2px; }
  .cover { display: block; width: 100%; position: relative; }
  strong { width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
  .description { font-size: var(--text-sm); color: var(--color-ink-muted); display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; min-height: 2.8em; }
  small { min-height: 1.4em; font-size: var(--text-xs); color: var(--color-ink-muted); }
  .add-chip { position: absolute; top: var(--space-2); right: var(--space-2); background: var(--color-panel); border-radius: var(--radius-md); padding: var(--space-1) var(--space-2); font-size: var(--text-sm); }
  @media (pointer: coarse) { .mix-shelf { scrollbar-width: none; } }
  @media (min-width: 900px) { .mix-shelf { grid-auto-columns: 184px; } }
</style>
