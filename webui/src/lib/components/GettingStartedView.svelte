<script lang="ts">
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button } from "$lib/components/ui/button";
  import { ChevronDown, Copy } from "@lucide/svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RouteError from "$lib/components/RouteError.svelte";
  import { toast } from "$lib/toasts";
  import { listenerSupport, musicClientUrl, reportHref, supportError, type ListenerContext } from "$lib/support";

  let { administrator = false }: { administrator?: boolean } = $props();
  let context = $state<ListenerContext | null>(null);
  let address = $state("");
  let error = $state("");
  let loading = $state(true);
  let addressField = $state<HTMLInputElement>();
  let usernameField = $state<HTMLInputElement>();
  const usableAddress = $derived(musicClientUrl(address));
  const webUrl = $derived(usableAddress ? `${usableAddress}/web/` : "");
  const apps = $derived(context ? [
    ...(context.backend === "Jellyfin" ? [
      { name: "Finer Player", platform: "iPhone", url: "",
        steps: ["Add a Jellyfin server using the address above.", "Sign in with your username and usual media-server password.", "Open Playlists to find your Spotify and made-for-you mixes."] },
      { name: "Musiver", platform: "Mobile", url: "",
        steps: ["Add a Jellyfin server using the address above.", "Sign in with your username and usual media-server password.", "Open a playlist and press play."] },
    ] : []),
    { name: "Feishin", platform: "Desktop", url: "https://github.com/jeffvli/feishin",
      steps: [`Open Manage servers and add a server. Choose ${context.backend === "Jellyfin" ? "Jellyfin" : "Subsonic"}.`,
        "Enter the music-client address and username above, then your usual media-server password.", "Search for a song and start listening."] },
    ...(context.backend === "Jellyfin" ? [
      { name: "Finamp", platform: "Android and iOS", url: "https://github.com/finamp-app/finamp",
        steps: ["Add a Jellyfin server using the music-client address above.", "Sign in with your username and usual media-server password.", "Choose your music library, then try a song and an offline download."] },
      { name: "Jellyfin Web", platform: "Web browser", url: "https://github.com/jellyfin/jellyfin-web",
        steps: ["Open the web player using the button below.", "Sign in with your username and usual media-server password.", "Open your music library and search for a song."] },
    ] : [
      { name: "Tempo", platform: "Android", url: "https://github.com/CappielloAntonio/tempo",
        steps: ["Add a Subsonic server using the music-client address above.", "Sign in with your username and usual media-server password.", "Refresh your library, then try search and playback."] },
    ]),
    { name: "Symfonium", platform: "Android · paid after a trial", url: context.backend === "Jellyfin"
      ? "https://docs.symfonium.app/wiki/providers/jellyfin-media-provider-configuration/"
      : "https://docs.symfonium.app/wiki/providers/subsonic-opensubsonic-media-provider-configuration/",
      steps: [`Add a ${context.backend === "Jellyfin" ? "Jellyfin" : "Subsonic / OpenSubsonic"} media provider.`,
        "Enter the music-client address, username and your usual media-server password.", "Sync the provider, then try a song and an offline cache."] },
  ] : []);

  async function load() {
    loading = !context;
    error = "";
    try {
      const next = await listenerSupport.context();
      if (!context || address === (context.musicClientUrl ?? "")) address = next.musicClientUrl ?? "";
      context = next;
    }
    catch (cause) { error = supportError(cause, "Connection details could not be loaded. Try again."); }
    finally { loading = false; }
  }
  async function copy(value: string, input?: HTMLInputElement) {
    if (!value) return;
    try {
      if (!navigator.clipboard?.writeText) throw new Error();
      await navigator.clipboard.writeText(value);
      toast.success("Copied.");
    } catch {
      input?.focus();
      input?.select();
      toast.info("Select and copy the highlighted text.");
    }
  }
  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { unsubscribe(); scheduler.cancel(); };
  });
</script>

{#if loading}
  <Skeleton class="settings-card skeleton-panel" aria-label="Loading connection details" aria-busy="true" />
{:else if error}
  <RouteError eyebrow="Connection guide unavailable" title="Connection details are unavailable." message={error} onRetry={load} />
{:else if context}
  <section class="settings-content connect-apps" aria-label="Listening guide">
    <header class="panel-heading"><div><h2>Listen in the app you already use</h2><p>Allstarr looks like a {context.backend === "Jellyfin" ? "Jellyfin" : "Subsonic"} server, so your music app connects to it with these two details and your usual media-server password.</p></div></header>

    <article class="settings-card connection-card">
      {#if context.addressSource === "unconfigured"}
        <p class="notice-info" role="status">Your administrator has not set a music-client address. Enter the address they gave you below.</p>
      {/if}
      <div class="connection-field">
        <label for="connect-address">Server address</label>
        <span class="connection-value">
          <input id="connect-address" bind:this={addressField} aria-label="Music-client address" type="url" spellcheck="false" autocomplete="off" bind:value={address} placeholder="https://music.example.com" />
          <Button variant="secondary" disabled={!usableAddress} aria-label="Copy server address" onclick={() => void copy(usableAddress!, addressField)}><Copy size={16} aria-hidden="true" />Copy</Button>
        </span>
        {#if address && !usableAddress}<p class="notice-info" role="status">Use an HTTP or HTTPS music-client URL without sign-in details, a query or a fragment.</p>{/if}
      </div>
      <div class="connection-field">
        <label for="connect-username">Username</label>
        <span class="connection-value">
          <input id="connect-username" bind:this={usernameField} aria-label="Username" value={context.username} readonly />
          <Button variant="secondary" aria-label="Copy username" onclick={() => void copy(context!.username, usernameField)}><Copy size={16} aria-hidden="true" />Copy</Button>
        </span>
      </div>
      {#if administrator}<footer><Button variant="outline" href="#/settings/access">Set the household address</Button></footer>{/if}
    </article>

    <div class="connect-section-title"><h3>Choose your app</h3><p>Open one for its steps.</p></div>
    <div class="connect-app-list">
      {#each apps as app, index (app.name)}
        <details class="settings-card connect-app" open={index === 0}>
          <summary>
            <span><h3>{app.name}</h3><small>{app.platform}</small></span>
            <ChevronDown class="disclosure-chevron" size={18} aria-hidden="true" />
          </summary>
          <ol>{#each app.steps as step}<li>{step}</li>{/each}</ol>
          {#if app.url || app.name === "Jellyfin Web"}
            <footer>
              {#if app.url}<Button variant="secondary" href={app.url} target="_blank" rel="noopener noreferrer">{app.name === "Jellyfin Web" ? "Web player help" : "Get the app"}</Button>{/if}
              {#if app.name === "Jellyfin Web"}<Button href={webUrl || undefined} disabled={!usableAddress} target="_blank" rel="noopener noreferrer">Open web player</Button>{/if}
            </footer>
          {/if}
        </details>
      {/each}
    </div>

    <article class="settings-card">
      <div class="settings-row"><div><strong>Coming from Spotify?</strong><small>Bring your playlists and liked songs. Nothing changes in Spotify.</small></div>
        <Button variant="secondary" href="#/library/import">Move your Spotify collection</Button></div>
      <div class="settings-row"><div><strong>Something not working?</strong><small>Use a song's report action to attach its details, or send a general note. Reports are saved for your administrator inside Allstarr.</small></div>
        <Button variant="secondary" href={reportHref()}>Report a problem</Button></div>
    </article>

    <details class="settings-card connect-app connect-tips">
      <summary><span><strong>Tips for getting more from Allstarr</strong><small>Hearts, offline listening and beta limits</small></span><ChevronDown class="disclosure-chevron" size={18} aria-hidden="true" /></summary>
      <div class="settings-row"><div><strong>Manual-only sources</strong><small>These appear in search and matching for you to choose. Automatic playback uses sources set to On.</small></div><Button variant="secondary" href="#/sources/services">View sources</Button></div>
      <div class="settings-row"><div><strong>Hearts and playlists</strong><small>Favorite provider songs or add them to your playlists to find them in normal browsing. Your listening history alone does not add them to that saved view.</small></div></div>
      <div class="settings-row"><div><strong>Offline listening</strong><small>Download or cache a song in your music app to keep a copy on that device. Check it plays with the device offline before you travel.</small></div></div>
      <div class="settings-row"><div><strong>Temporary source limits</strong><small>Some services can rate-limit requests or ask for verification. Choose another available source or wait a few minutes.</small></div></div>
      {#if context.backend === "Jellyfin"}<div class="settings-row"><div><strong>Finamp formats</strong><small>Some transcoded formats are unavailable. Include the song and app version when reporting a format error.</small></div></div>{/if}
    </details>
  </section>
{/if}

<style>
  .connection-card { display: grid; gap: var(--space-4); padding: var(--surface-gutter); }
  .connection-card :global(footer) { padding: 0; border: 0; }
  .connection-field { display: grid; gap: var(--space-2); }
  .connection-field label { font-size: var(--text-sm); font-weight: 600; }
  .connection-value { display: flex; min-width: 0; gap: var(--space-2); align-items: center; }
  .connection-value input { flex: 1; min-width: 0; min-height: var(--control-md); padding-inline: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--md-sys-color-surface-container-lowest); color: var(--color-ink); }
  .connect-apps > :global(.panel-heading) { padding-inline: 0; border: 0; }
  .connect-section-title { display: flex; align-items: baseline; gap: var(--space-2); margin-top: var(--space-2); }
  .connect-section-title h3 { font-size: 1.125rem; font-weight: 600; }
  .connect-section-title p { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .connect-app-list { display: grid; gap: var(--space-2); grid-template-columns: repeat(auto-fit, minmax(min(100%, 22rem), 1fr)); align-items: start; }
  .connect-app > summary { display: flex; min-height: 4rem; align-items: center; justify-content: space-between; gap: var(--space-3); padding: var(--space-3) var(--surface-gutter); cursor: pointer; list-style: none; border-radius: inherit; transition: background-color 160ms var(--ease-standard); }
  .connect-app > summary::-webkit-details-marker { display: none; }
  @media (hover: hover) { .connect-app > summary:hover { background: var(--row-hover); } }
  .connect-app > summary > span { display: grid; min-width: 0; }
  .connect-app > summary h3 { font-size: var(--text-md); font-weight: 600; }
  .connect-app > summary small { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .connect-app[open] > summary :global(.disclosure-chevron) { transform: rotate(180deg); }
  .connect-app ol { display: grid; gap: var(--space-2); padding: 0 var(--surface-gutter) var(--space-4) calc(var(--surface-gutter) + 1.1rem); list-style: decimal; font-size: var(--text-sm); color: var(--color-ink-muted); }
  .connect-app footer { display: flex; flex-wrap: wrap; gap: var(--space-2); padding: 0 var(--surface-gutter) var(--surface-gutter); border: 0; }
</style>
