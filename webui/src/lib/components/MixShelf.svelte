<script lang="ts">
  import type { MixCard } from "$lib/api";
  import { mixDescription, mixName, shortReadiness } from "$lib/for-you";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import MixCover from "$lib/components/MixCover.svelte";
  let { title, cards, onopen, seeAll = "", additions = [], albumArt = false, busy = "", onadd }: { title: string; cards: MixCard[]; onopen: (card: MixCard) => void; seeAll?: string; additions?: string[]; albumArt?: boolean; busy?: string; onadd?: (card: MixCard) => void } = $props();
</script>
{#snippet content(card: MixCard)}
  <span class="artwork" class:unready={!card.readiness.ready}>{#if albumArt}<MixArtwork references={card.coverArtwork} />{:else}<MixCover mix={card} />{/if}</span>
  <strong>{mixName(card.name)}</strong><span class="description">{card.readiness.ready ? mixDescription(card.description, card.kind) : shortReadiness(card.readiness.message)}</span>
  <small>{card.inApps ? "✓ In your apps" : ""}</small>
{/snippet}
<section>
  <header><h2>{title}</h2>{#if seeAll}<a href={seeAll}>See all</a>{/if}</header>
  <div class="mix-shelf" role="region" aria-label={title}>
    {#each cards as card, index (`${index}:${card.id}`)}
      <div class="mix-card" role="group" aria-label={mixName(card.name)}>
        <div class="cover">
          <button type="button" class="mix-open" aria-label={`${additions.includes(card.id) ? "Add" : "Open"} ${mixName(card.name)}`} disabled={additions.includes(card.id) && (Boolean(busy) || !card.appsAllowed)} onclick={() => onopen(card)}>{@render content(card)}</button>
          {#if onadd && !card.inApps && card.appsAllowed && card.readiness.ready}
            <button type="button" class="add-chip" aria-label={`Add ${mixName(card.name)}`} disabled={Boolean(busy)} onclick={() => onadd?.(card)}>{busy === card.id ? "Adding…" : "Add to apps"}</button>
          {/if}
        </div>
      </div>
    {/each}
  </div>
</section>
<style>
  section { min-width: 0; }
  header { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); margin-bottom: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  a { display: inline-flex; min-height: 44px; align-items: center; }
  .mix-shelf { padding-top: 6px; display: grid; grid-auto-flow: column; grid-auto-columns: 160px; overflow-x: auto; scroll-snap-type: x mandatory; gap: 12px; padding-bottom: var(--space-2); }
  .mix-card { scroll-snap-align: start; display: flex; flex-direction: column; gap: var(--space-2); min-width: 0; text-align: left; color: var(--color-ink); border-radius: var(--radius-md); }
  .mix-open { display: flex; flex-direction: column; gap: var(--space-2); width: 100%; text-align: left; color: inherit; border-radius: inherit; }
  button.mix-open { cursor: pointer; }
  .mix-open:focus-visible { outline: 2px solid var(--color-signal); outline-offset: -2px; }
  .cover { width: 100%; position: relative; border-radius: inherit; transition: transform 280ms var(--ease-emphasized), filter 280ms var(--ease-standard); }
  @media (hover: hover) { .mix-card:hover .cover { transform: translateY(-3px) scale(1.015); filter: drop-shadow(0 8px 14px rgb(0 0 0 / 28%)); } }
  .mix-card:active .cover { transform: scale(0.985); }
  .artwork { display: block; width: 100%; }
  .artwork.unready { opacity: 0.6; }
  small { min-height: 1.4em; font-size: var(--text-xs); color: var(--color-ink-muted); }
  .add-chip { position: absolute; top: var(--space-2); right: var(--space-2); background: var(--color-panel); border-radius: var(--radius-md); padding: var(--space-1) var(--space-2); font-size: var(--text-sm); cursor: pointer; }
  .add-chip:hover { background: color-mix(in srgb, var(--color-ink) 8%, var(--color-panel)); }
  .add-chip:focus-visible { outline: 2px solid var(--color-signal); outline-offset: 2px; }
  .add-chip:disabled { cursor: default; }
  @media (pointer: coarse) { .add-chip::after { content: ""; position: absolute; min-width: 44px; min-height: 44px; inset: 50% auto auto 50%; transform: translate(-50%, -50%); } }
  strong { width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
  .description { font-size: var(--text-sm); color: var(--color-ink-muted); display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; min-height: 2.8em; }
  @media (pointer: coarse) { .mix-shelf { scrollbar-width: none; } }
  @media (min-width: 900px) { .mix-shelf { grid-auto-columns: 184px; } }
</style>
