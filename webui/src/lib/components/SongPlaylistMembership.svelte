<script lang="ts">
  import { playlistLinks, type ManagedDownload, type SongPlaylistIdentity } from "$lib/api";
  import { canonicalProviderId } from "$lib/sources";

  let { file, open = false }: { file: ManagedDownload; open?: boolean } = $props();
  let playlists = $state<{ id: string; name: string }[]>([]);
  let loading = $state(false), unavailable = $state(false);
  const identityKey = $derived(JSON.stringify({
    canonicalRecordingId: file.canonicalRecordingId ?? null,
    providerId: file.provider ? canonicalProviderId(file.provider) : null,
    externalId: file.externalId ?? null,
    protocol: file.protocol ?? null,
    backendInstanceId: file.backendInstanceId ?? null,
    backendItemId: file.backendItemId ?? null,
  } satisfies SongPlaylistIdentity));

  $effect(() => {
    if (!open) return;
    const song = JSON.parse(identityKey) as SongPlaylistIdentity;
    const request = new AbortController();
    let active = true;
    playlists = []; loading = true; unavailable = false;
    void (async () => {
      try {
        const result = await playlistLinks.membership(song, request.signal);
        if (active) playlists = result.playlists;
      } catch { if (active) unavailable = true; }
      finally { if (active) loading = false; }
    })();
    return () => { active = false; request.abort(); };
  });
</script>

<section aria-label="In your playlists" aria-busy={loading}>
  <h3>In your playlists</h3>
  {#if loading}<p class="empty-playlists">Loading playlists…</p>
  {:else}{#each playlists as playlist (playlist.id)}<a class="playlist-link" href={`#/library/playlists?playlist=${encodeURIComponent(playlist.id)}`}>{playlist.name}<span aria-hidden="true">›</span></a>
  {:else}<p class="empty-playlists">{unavailable ? "Playlist membership could not be loaded." : "Not in any of your playlists"}</p>{/each}{/if}
</section>

<style>
  h3 { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; letter-spacing: .06em; text-transform: uppercase; }
  .playlist-link { display: flex; align-items: center; justify-content: space-between; min-height: 44px; border-bottom: 1px solid var(--color-edge); color: var(--color-signal-text); font-size: var(--text-sm); }
  .empty-playlists { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
</style>
