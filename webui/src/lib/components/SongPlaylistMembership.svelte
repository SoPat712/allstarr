<script lang="ts">
  import { playlistLinks, type ManagedDownload } from "$lib/api";
  import { canonicalProviderId } from "$lib/sources";

  let { file, open = false }: { file: ManagedDownload; open?: boolean } = $props();
  let playlists = $state<{ id: string; name: string }[]>([]);
  let loading = $state(false), unavailable = $state(false);

  $effect(() => {
    const song = file;
    if (!open) return;
    let active = true;
    playlists = []; loading = true; unavailable = false;
    void (async () => {
      try {
        const links = (await playlistLinks.list()).playlistLinks ?? [];
        const found: { id: string; name: string }[] = [];
        for (const link of links) {
          if (!active) return;
          const detail = await playlistLinks.details(link.id);
          if (detail.tracks?.some(track => track.providerRoutes?.some(route => canonicalProviderId(route.providerId) === canonicalProviderId(song.provider ?? "") && route.externalId === song.externalId)))
            found.push({ id: link.id, name: link.name });
        }
        if (active) playlists = found;
      } catch { if (active) unavailable = true; }
      finally { if (active) loading = false; }
    })();
    return () => { active = false; };
  });
</script>

<section aria-label="In your playlists" aria-busy={loading}>
  <h3>In your playlists</h3>
  {#if loading}<p class="empty-playlists">Loading playlists…</p>
  {:else}{#each playlists as playlist (playlist.id)}<a class="playlist-link" href={`#/library/playlists?id=${encodeURIComponent(playlist.id)}`}>{playlist.name}<span aria-hidden="true">›</span></a>
  {:else}<p class="empty-playlists">{unavailable ? "Playlist membership could not be loaded." : "No playlists found for this copy."}</p>{/each}{/if}
</section>

<style>
  h3 { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; letter-spacing: .06em; text-transform: uppercase; }
  .playlist-link { display: flex; align-items: center; justify-content: space-between; min-height: 44px; border-bottom: 1px solid var(--color-edge); color: var(--color-signal-text); font-size: var(--text-sm); }
  .empty-playlists { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
</style>
