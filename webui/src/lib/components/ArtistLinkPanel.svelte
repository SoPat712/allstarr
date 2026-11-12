<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { artistScopes, artistSections, inAppsNow, serviceDetail, type ArtistLinkRow, type ArtistLinkScope, type ArtistLinkService } from "$lib/artist-links";
  import { countLabel } from "$lib/count-label";
  import { formatLocalDateTime } from "$lib/dates";
  import { findProviderDefinition } from "$lib/sources";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import SplitButton from "$lib/components/patterns/SplitButton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import { Button } from "$lib/components/ui/button";

  let { open = $bindable(false), artist, providers, loading = false, error = "", busy = "", onanswer, onundo, onretry, onPrevious, onNext }: {
    open?: boolean;
    artist: ArtistLinkRow | null;
    providers: ProviderDefinition[];
    loading?: boolean;
    error?: string;
    busy?: string;
    onanswer: (service: ArtistLinkService, same: boolean, scope: ArtistLinkScope) => void;
    onundo: (service: ArtistLinkService) => void;
    onretry: () => void;
    onPrevious?: () => void;
    onNext?: () => void;
  } = $props();

  // One choice of who an answer is for covers both buttons, as song matches do.
  let scope = $state<string>("household");
  const sections = $derived(artist ? artistSections(artist) : []);
  const asking = $derived(sections.some(section => section.id === "ask"));
  const checkedAt = $derived(artist?.services.find(service => service.checkedAt)?.checkedAt);
  const key = (service: ArtistLinkService) => `${service.providerId}:${service.artistId}`;
</script>

<SidePanel bind:open title={artist?.name ?? "Artist"} description={artist ? `${countLabel(artist.songCount, "song")} in your library` : undefined} navigationLabel="artist" {onPrevious} {onNext}>
  {#snippet artwork()}<MediaArtwork class="artist-art" url={artist?.imageUrl} fallback={artist?.name.trim()[0]?.toUpperCase() ?? "?"} />{/snippet}
  {#if error}<ErrorState title="This artist could not load" detail={error} onretry={onretry} />
  {:else if !artist || loading && !artist.services.length}<RowsSkeleton count={3} />
  {:else}
    <div class="artist-links">
      {#if asking}<p class="how">
        <strong>How this works.</strong> When a service artist shares songs with your library artist, Allstarr treats them as one artist: apps show one entry with every song from both. Without shared songs it keeps them apart, so a different person with the same name never gets mixed in.
      </p>{/if}
      {#each sections as section (section.id)}
        <section aria-label={section.label}>
          <h3 class="sec-label">{section.label}</h3>
          <ul class="links">
            {#each section.services as service (key(service))}
              <li class="link" class:ask={section.id === "ask"}>
                <ProviderMark id={service.providerId} definition={findProviderDefinition(providers, service.providerId)} label={service.providerName} />
                <div class="link-copy"><strong>{service.name} · {service.providerName}</strong><small>{serviceDetail(service)}</small></div>
                <div class="link-actions">
                  {#if section.id === "ask"}
                    <SplitButton label="Same artist" options={artistScopes} bind:value={scope} disabled={Boolean(busy)} onclick={() => onanswer(service, true, scope as ArtistLinkScope)} />
                    <Button variant="outline" size="sm" disabled={Boolean(busy)} onclick={() => onanswer(service, false, scope as ArtistLinkScope)}>Not the same</Button>
                  {:else if section.id === "songs"}
                    <Button variant="outline" size="sm" disabled={Boolean(busy)} onclick={() => onanswer(service, false, scope as ArtistLinkScope)}>Not the same</Button>
                  {:else}
                    <Button variant="text" size="sm" disabled={Boolean(busy)} onclick={() => onundo(service)}>Undo</Button>
                  {/if}
                </div>
              </li>
            {/each}
          </ul>
        </section>
      {:else}<p class="none">No service has an artist by this name yet. Allstarr looks again when it next checks your artists.</p>{/each}
      <dl>
        <dt>In apps now</dt><dd>{inAppsNow(artist)}</dd>
        {#if checkedAt}<dt>Checked</dt><dd><time datetime={checkedAt}>{formatLocalDateTime(checkedAt)}</time></dd>{/if}
      </dl>
    </div>
  {/if}
</SidePanel>

<style>
  :global(.artist-art) { border-radius: 50%; }
  .artist-links { display: grid; gap: var(--space-5); }
  .how { margin: 0; border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-panel-raised); color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  .how strong { color: var(--color-ink); font-weight: 600; }
  .links { display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none; }
  .link { display: grid; grid-template-columns: 32px minmax(0, 1fr); gap: var(--space-2) var(--space-3); align-items: center; border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); }
  .link.ask { border-color: color-mix(in srgb, var(--warning) 45%, var(--color-edge)); box-shadow: inset 3px 0 var(--warning); }
  .link :global(.provider-mark) { width: 32px; height: 32px; }
  .link-copy { min-width: 0; }
  .link-copy strong { display: block; font-weight: 500; overflow-wrap: anywhere; }
  .link-copy small { display: block; margin-top: 2px; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.45; }
  .link-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); align-items: center; grid-column: 2; }
  .link-actions :global(button) { min-height: 36px; height: auto; padding-inline: var(--space-3); font-size: .8125rem; }
  @media (max-width: 899px) { .link-actions :global(button) { min-height: 44px; } }
  .none { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  dl { display: grid; grid-template-columns: minmax(0, .8fr) minmax(0, 1.8fr); gap: var(--space-3); margin: 0; font-size: var(--text-sm); line-height: 1.5; }
  dt { color: var(--color-ink-muted); }
  dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }
</style>
