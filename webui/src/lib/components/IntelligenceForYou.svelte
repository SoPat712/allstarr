<script lang="ts">
  import { onMount } from "svelte";
  import { playlistLinks, type PlaylistDiscoveryItem, type PlaylistLink, type PlaylistSourceAccount } from "$lib/api";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { formatCount, lastFmPeriods, playlistSourceKey, brainzDetails, brainzPlaylists } from "$lib/intelligence";
  import { relativeTime } from "$lib/activity";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import AddPlaylistDialog from "$lib/components/AddPlaylistDialog.svelte";

  let accounts = $state<PlaylistSourceAccount[]>([]);
  let links = $state<PlaylistLink[]>([]);
  let brainz = $state<PlaylistDiscoveryItem[]>([]);
  let brainzCursor = $state("");
  let notices = $state<Record<string, string>>({});
  let loading = $state(true), fetching = false, request = 0;
  let loadedPages = 1, loadedAccount = "";
  let error = $state(""), period = $state("1month"), open = $state(false);
  let initialSource = $state<{ accountId: string; playlistId: string; title: string; followNewestKind?: string } | null>(null);
  const brainzAccount = $derived(accounts.find(item => item.providerId === "listenbrainz" && item.scope === "personal"));
  const lastFmAccount = $derived(accounts.find(item => item.providerId === "lastfm" && item.scope === "personal"));
  const brainzCards = $derived(brainzPlaylists(brainz));
  const lastFmCards = $derived([
    { id: "loved", name: "Loved on Last.fm", subtitle: "Your loved songs, together." },
    { id: `top:${period}:50`, name: "Your top tracks", subtitle: "The songs you return to." },
    { id: "similar", name: "Similar to your top tracks", subtitle: "Suggestions from Last.fm" },
  ]);

  function linked(provider: string, id: string, kind = "") {
    const account = provider === "listenbrainz" ? brainzAccount : lastFmAccount;
    return links.find(item => item.sourceProviderId === provider && (playlistSourceKey(provider, item.sourcePlaylistId) === playlistSourceKey(provider, id) || kind && item.sourcePlaylistId === `createdfor:${kind}`) &&
      (item.providerAccountId === account?.id || provider === "listenbrainz" && item.providerAccountId === null));
  }

  async function load() {
    if (fetching) return;
    fetching = true;
    const current = ++request;
    try {
      const [sources, saved] = await Promise.all([playlistLinks.sources(), playlistLinks.list()]);
      if (current !== request) return;
      accounts = sources.accounts;
      links = saved.playlistLinks;
      if ((brainzAccount?.id ?? "") !== loadedAccount) { loadedPages = 1; brainz = []; brainzCursor = ""; loadedAccount = brainzAccount?.id ?? ""; }
      const results = await Promise.all(accounts.filter(item => item.id === brainzAccount?.id || item.id === lastFmAccount?.id).map(async account => {
        try {
          let value = await playlistLinks.sourcePlaylists(account.id);
          for (let page = 1; account.providerId === "listenbrainz" && page < loadedPages && value.nextCursor; page++) {
            const next = await playlistLinks.sourcePlaylists(account.id, "", value.nextCursor);
            value = { ...next, items: [...value.items, ...next.items.filter(item => !value.items.some(existing => existing.id === item.id))] };
          }
          return { account, value, notice: "" };
        }
        catch (cause) {
          const message = cause instanceof Error ? cause.message : "Playlists could not be loaded. Try again later.";
          return { account, value: null, notice: message };
        }
      }));
      if (current !== request) return;
      const nextNotices: Record<string, string> = {};
      for (const result of results) {
        nextNotices[result.account.providerId] = result.notice;
        if (result.account.id === brainzAccount?.id && result.value) {
          brainz = result.value.items;
          brainzCursor = result.value.nextCursor ?? "";
        }
      }
      notices = nextNotices;
      error = "";
    } catch { if (current === request) error = "Your playlists could not be loaded. Try again."; }
    finally { if (current === request) { loading = false; fetching = false; } }
  }

  async function more() {
    if (!brainzAccount || !brainzCursor || fetching) return;
    fetching = true;
    const current = request;
    try {
      const result = await playlistLinks.sourcePlaylists(brainzAccount.id, "", brainzCursor);
      if (current !== request) return;
      brainz = [...brainz, ...result.items.filter(item => !brainz.some(existing => existing.id === item.id))];
      brainzCursor = result.nextCursor ?? "";
      loadedPages++;
    } catch { if (current === request) toast.error("More playlists could not be loaded."); }
    finally { fetching = false; }
  }

  function add(account: PlaylistSourceAccount, id: string, title: string, followNewestKind = "") {
    initialSource = { accountId: account.id, playlistId: id, title, ...(followNewestKind ? { followNewestKind } : {}) };
    open = true;
  }

  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { request++; unsubscribe(); scheduler.cancel(); };
  });
</script>

<div class="for-you">
  <div class="section-heading"><p>Playlists from your listening, ready to make your own.</p><RefreshButton variant="secondary" label="Refresh playlists" onrefresh={load} /></div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading your playlists" />
  {:else}
    <section aria-labelledby="brainz-heading">
      <h2 id="brainz-heading">From ListenBrainz</h2>
      {#if brainzAccount && brainz.length && !brainzCursor && !notices.listenbrainz && !brainzCards.some(item => brainzDetails(item.description).kind)}<p>ListenBrainz hasn't made playlists for you yet. They appear after about a week of listening.</p>{/if}
      <div class="playlist-cards">
        {#if !brainzAccount}
          <article class="settings-card"><h3>Connect ListenBrainz to see playlists made from your listening</h3><Button variant="secondary" href="#/integrations/accounts?source=listenbrainz&connect=1">Connect ListenBrainz</Button></article>
        {:else if notices.listenbrainz}
          <article class="settings-card"><p>{notices.listenbrainz}</p><Button variant="secondary" href="#/integrations/accounts?source=listenbrainz">Open Sources</Button></article>
        {:else if !brainz.length}
          <article class="settings-card"><p>ListenBrainz hasn't made playlists for you yet. They appear after about a week of listening.</p></article>
        {:else}
          {#each brainzCards as item (item.id)}
            {@const details = brainzDetails(item.description)}
            {@const saved = linked("listenbrainz", item.id, details.kind)}
            <article class="settings-card" aria-label={item.name}>
              <div><h3>{item.name}</h3><p>ListenBrainz · {details.created ? `created ${relativeTime(details.created)}` : "Date unavailable"}</p></div>
              {#if item.trackCount != null}<p>{`${formatCount(item.trackCount)} ${item.trackCount === 1 ? "song" : "songs"}`}</p>{/if}
              <div class="card-actions">{#if saved}<span>Added ✓</span><Button variant="secondary" href={`#/library/playlists?playlist=${encodeURIComponent(saved.id)}`}>Open</Button>
              {:else}<Button variant="secondary" onclick={() => brainzAccount && add(brainzAccount, item.id, item.name, details.kind)}>Add to my playlists</Button>{/if}</div>
            </article>
          {/each}
        {/if}
      </div>
      {#if brainzCursor}<Button variant="secondary" onclick={more}>More playlists</Button>{/if}
    </section>
    <section aria-labelledby="lastfm-heading">
      <h2 id="lastfm-heading">From Last.fm</h2>
      {#if !lastFmAccount}
        <article class="settings-card"><h3>Connect Last.fm to bring your listening into playlists</h3><Button variant="secondary" href="#/integrations/accounts?source=lastfm&connect=1">Connect Last.fm</Button></article>
      {:else if notices.lastfm}
        <article class="settings-card"><p>{notices.lastfm}</p><Button variant="secondary" href="#/integrations/accounts?source=lastfm">Open Sources</Button></article>
      {:else}
        <div class="playlist-cards">
          {#each lastFmCards as item (item.name)}
            {@const saved = linked("lastfm", item.id)}
            <article class="settings-card" aria-label={item.name}>
              <div><h3>{item.name}</h3><p>{item.subtitle}</p></div>
              {#if item.name === "Your top tracks"}<SelectField bind:value={period} label="Top tracks period" options={lastFmPeriods} />{/if}
              <div class="card-actions">{#if saved}<span>Added ✓</span><Button variant="secondary" href={`#/library/playlists?playlist=${encodeURIComponent(saved.id)}`}>Open</Button>
              {:else}<Button variant="secondary" onclick={() => lastFmAccount && add(lastFmAccount, item.id, item.name)}>Add to my playlists</Button>{/if}</div>
            </article>
          {/each}
        </div>
      {/if}
    </section>
  {/if}
</div>
<AddPlaylistDialog bind:open providers={[]} {initialSource} onSaved={async message => { toast.success(message); await load(); }} />

<style>
  .for-you { display: grid; gap: var(--space-6); min-width: 0; }
  .section-heading, .card-actions { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .section-heading p, article p { color: var(--color-ink-muted); }
  h2 { margin-bottom: var(--space-4); font-size: 1.125rem; font-weight: 600; }
  h3 { font-weight: 600; overflow-wrap: anywhere; }
  .playlist-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 15rem), 1fr)); gap: var(--section-gap); margin-bottom: var(--space-3); }
  .settings-card { display: flex; flex-direction: column; align-items: start; gap: var(--space-4); padding: var(--surface-gutter); min-width: 0; }
  .card-actions { margin-top: auto; width: 100%; }
</style>
