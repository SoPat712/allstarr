<script lang="ts">
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button } from "$lib/components/ui/button";
  import { ChevronDown, Copy, Globe, Headphones, Monitor, Music2, Smartphone } from "@lucide/svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import { toast } from "$lib/toasts";
  import { listenerSupport, musicClientUrl, reportHref, supportError, type ListenerContext } from "$lib/support";

  let { administrator = false }: { administrator?: boolean } = $props();
  let context = $state<ListenerContext | null>(null);
  let address = $state("");
  let error = $state("");
  let loading = $state(true);
  let selectedApp = $state("");
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

  const activeApp = $derived(apps.find(app => app.name === selectedApp) ?? apps[0]);

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
  <RowsSkeleton count={4} />
{:else if error && !context}
  <ErrorState title="Connection details are unavailable." detail={error} onretry={load} />
{:else if context}
  <section class="settings-content connect-apps" aria-label="Listening guide">
    <header class="panel-heading"><p>Allstarr works inside the music apps you already use. Pick yours.</p></header>

    <PageStatus label="Connection status" items={[
      { label: `${context.backend === "Jellyfin" ? "Jellyfin" : "Subsonic"} apps` },
      { label: usableAddress ? "Server address ready to copy" : "Enter your server address", tone: usableAddress ? "good" : "warn" },
    ]} />
    {#if error}<ErrorState title="Connection details could not be refreshed." detail={error} onretry={load} />{/if}
    <div class="connect-section-title"><h3>Choose your app</h3><p>Pick one to see its steps.</p></div>
    <div class="connect-app-list" aria-label="Compatible music apps">
      {#each apps as app (app.name)}
        <button class="settings-card app-choice" class:selected={activeApp?.name === app.name} type="button"
          aria-pressed={activeApp?.name === app.name} aria-label={`${app.name} ${app.platform}`} aria-controls="app-guide"
          onclick={() => selectedApp = app.name}>
          <span class="app-icon" aria-hidden="true">
            {#if app.name === "Jellyfin Web"}<Globe size={25} />
            {:else if app.name === "Feishin"}<Monitor size={25} />
            {:else if app.name === "Symfonium"}<Headphones size={25} />
            {:else if app.name === "Finamp" || app.name === "Tempo"}<Smartphone size={25} />
            {:else}<Music2 size={25} />{/if}
          </span>
          <span class="app-name">{app.name}</span><small>{app.platform}</small>
        </button>
      {/each}
    </div>
    {#if activeApp}
      <section class="settings-card app-guide" id="app-guide" aria-labelledby="app-guide-title">
        <header><h3 id="app-guide-title">Set up {activeApp.name}</h3></header>
    <div class="connection-card">
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
    </div>

        <ol class="app-steps" aria-label="Connection steps">{#each activeApp.steps as step, index}<li><span class="step-circle" aria-hidden="true">{index + 1}</span><span>{step}</span></li>{/each}</ol>
        {#if activeApp.url || activeApp.name === "Jellyfin Web"}
          <footer>
            {#if activeApp.url}<Button variant="text" href={activeApp.url} target="_blank" rel="noopener noreferrer">{activeApp.name === "Jellyfin Web" ? "Web player help" : "Get the app"}</Button>{/if}
            {#if activeApp.name === "Jellyfin Web"}<Button href={webUrl || undefined} disabled={!usableAddress} target="_blank" rel="noopener noreferrer">Open web player</Button>{/if}
          </footer>
        {/if}
      </section>
    {:else}<EmptyState title="No compatible apps available" description="Try refreshing your connection details." />{/if}

    <div class="connect-help">
      <article class="settings-card help-card">
        <h3>Coming from Spotify or Apple Music?</h3><p>Bring your playlists and liked songs. Nothing changes there.</p>
        <Button variant="ghost" href="#/library/import">Import your music</Button>
      </article>
      <article class="settings-card help-card">
        <h3>Something not working?</h3><p>Use a song's report action to attach its details, or send a general note. Reports stay inside Allstarr for your administrator.</p>
        <Button variant="ghost" href={reportHref()}>Report a problem</Button>
      </article>
    <details class="settings-card connect-tips help-card">
      <summary><span><strong>Tips</strong><small>Hearts, offline listening and beta limits</small><span class="tips-action">Read the tips</span></span><ChevronDown class="disclosure-chevron" size={18} aria-hidden="true" /></summary>
      <div class="settings-row"><div><strong>Manual-only sources</strong><small>These appear in search and matching for you to choose. Automatic playback uses sources set to On.</small></div><Button variant="secondary" href="#/sources/services">View sources</Button></div>
      <div class="settings-row"><div><strong>Hearts and playlists</strong><small>Favorite provider songs or add them to your playlists to find them in normal browsing. Your listening history alone does not add them to that saved view.</small></div></div>
      <div class="settings-row"><div><strong>Offline listening</strong><small>Download or cache a song in your music app to keep a copy on that device. Check it plays with the device offline before you travel.</small></div></div>
      <div class="settings-row"><div><strong>Temporary source limits</strong><small>Some services can rate-limit requests or ask for verification. Choose another available source or wait a few minutes.</small></div></div>
      {#if context.backend === "Jellyfin"}<div class="settings-row"><div><strong>Finamp formats</strong><small>Some transcoded formats are unavailable. Include the song and app version when reporting a format error.</small></div></div>{/if}
    </details>
    </div>
  </section>
{/if}

<style>
  .connect-apps { min-width: 0; }
  .connection-card { display: grid; gap: var(--space-4); padding: var(--surface-gutter); border-bottom: 1px solid var(--color-edge); }
  .connection-card :global(footer) { padding: 0; border: 0; }
  .connection-field { display: grid; gap: var(--space-2); }
  .connection-field label { font-size: var(--text-sm); font-weight: 600; }
  .connection-value { display: flex; min-width: 0; gap: var(--space-2); align-items: center; }
  .connection-value input { flex: 1; min-width: 0; min-height: var(--control-md); padding-inline: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--md-sys-color-surface-container-lowest); color: var(--color-ink); }
  .connect-apps > :global(.panel-heading) { padding-inline: 0; border: 0; }
  .connect-section-title { display: flex; flex-wrap: wrap; align-items: baseline; gap: var(--space-2); }
  .connect-section-title h3 { font-size: var(--text-md); font-weight: 600; }
  .connect-section-title p { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .connect-app-list { display: grid; gap: var(--space-3); grid-template-columns: repeat(auto-fit, minmax(min(100%, 8rem), 1fr)); }
  .app-choice { display: flex; min-width: 0; min-height: 9rem; align-items: center; justify-content: center; flex-direction: column; gap: var(--space-2); padding: var(--space-4) var(--space-2); color: var(--color-ink); font: inherit; text-align: center; cursor: pointer; transition: background var(--motion-standard), border-color var(--motion-standard); }
  .app-choice:hover { background: var(--row-hover); }
  .app-choice.selected { border-color: var(--color-signal); outline: 1px solid var(--color-signal); }
  .app-choice:focus-visible, summary:focus-visible { outline: 2px solid var(--color-ring); outline-offset: 2px; }
  .app-icon { display: grid; width: var(--control-lg); height: var(--control-lg); place-items: center; border-radius: var(--radius-lg); background: var(--color-panel-raised); color: var(--color-signal); }
  .app-name { font-size: var(--text-sm); font-weight: 600; }
  .app-choice small { color: var(--color-ink-muted); font-size: var(--text-xs); overflow-wrap: anywhere; }
  .app-guide { min-width: 0; overflow: hidden; }
  .app-guide ol { margin: 0; padding: 0; list-style: none; font-size: var(--text-sm); color: var(--color-ink); }
  .app-guide li { display: flex; align-items: center; gap: var(--space-4); padding: var(--space-4); border-bottom: 1px solid var(--color-edge); }
  .app-guide li:last-child { border-bottom: 0; }
  .step-circle { display: grid; width: 26px; height: 26px; flex: 0 0 auto; place-items: center; border-radius: 50%; background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); font-size: var(--text-xs); font-weight: 600; }
  .app-guide footer { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .connect-help { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); align-items: start; gap: var(--space-3); }
  .help-card { min-width: 0; padding: var(--space-4); }
  .help-card h3, .help-card strong { margin: 0; font-size: var(--text-sm); font-weight: 600; }
  .help-card p { margin: var(--space-2) 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  .help-card :global(a) { max-width: 100%; white-space: normal; }
  .connect-tips > summary { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-2); list-style: none; cursor: pointer; }
  .connect-tips > summary::-webkit-details-marker { display: none; }
  .connect-tips > summary > span { display: grid; gap: var(--space-2); }
  .connect-tips small { color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  .tips-action { display: flex; min-height: var(--control-md); align-items: center; color: var(--color-signal); font-size: var(--text-sm); font-weight: 600; }
  .connect-tips[open] > summary :global(.disclosure-chevron) { transform: rotate(180deg); }
  .connect-tips .settings-row { padding-inline: 0; }
  @media (max-width: 760px) { .connect-help { grid-template-columns: minmax(0, 1fr); } }
  @media (prefers-reduced-motion: reduce) { .app-choice { transition: none; } }
</style>
