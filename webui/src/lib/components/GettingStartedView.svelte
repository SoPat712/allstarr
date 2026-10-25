<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
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
    loading = true;
    error = "";
    try { context = await listenerSupport.context(); address = context.musicClientUrl ?? ""; }
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
  onMount(() => { void load(); });
</script>

{#if loading}
  <Skeleton class="settings-card skeleton-panel" aria-label="Loading connection details" aria-busy="true" />
{:else if error}
  <RouteError eyebrow="Connection guide unavailable" title="Connection details are unavailable." message={error} onRetry={load} />
{:else if context}
  <section class="settings-content" aria-label="Listening guide">
    <header class="panel-heading"><div><h2>Your music apps</h2><p>Connect your usual music app to Allstarr and sign in with your media-server account.</p></div></header>
    <article class="settings-card">
      <header><div><h3>Connection details</h3><small>This server uses {context.backend === "Jellyfin" ? "Jellyfin" : "Subsonic / OpenSubsonic"}.</small></div></header>
      {#if context.addressSource === "unconfigured"}
        <p class="notice-info" role="status">Your administrator has not set a music-client address. Enter the address they gave you below.</p>
      {/if}
      <label class="settings-row setting-field"><div><strong>Music-client address</strong><small>Editing this guide changes only the address you copy.</small></div>
        <input bind:this={addressField} aria-label="Music-client address" type="url" spellcheck="false" autocomplete="off" bind:value={address} placeholder="https://music.example.com" /></label>
      {#if address && !usableAddress}<p class="notice-info" role="status">Use an HTTP or HTTPS music-client URL without sign-in details, a query or a fragment.</p>{/if}
      <div class="settings-row"><div><strong>Copy the server address</strong><small>Use it in the app's server field.</small></div>
        <Button variant="secondary" disabled={!usableAddress} onclick={() => void copy(usableAddress!, addressField)}>Copy server address</Button></div>
      <label class="settings-row setting-field"><div><strong>Username</strong><small>Sign in with your usual media-server password.</small></div>
        <input bind:this={usernameField} aria-label="Username" value={context.username} readonly /></label>
      <div class="settings-row"><div><strong>Copy your username</strong><small>Your signed-in media-server account.</small></div>
        <Button variant="secondary" onclick={() => void copy(context!.username, usernameField)}>Copy username</Button></div>
      {#if administrator}<footer><Button variant="secondary" href="#/settings/network">Set the household address</Button></footer>{/if}
    </article>

    {#each apps as app (app.name)}
      <article class="settings-card">
        <header><div><h3>{app.name}</h3><small>{app.platform}</small></div></header>
        <ol class="settings-impact space-y-2">{#each app.steps as step, index}<li>{index + 1}. {step}</li>{/each}</ol>
        <footer><Button variant="secondary" href={app.url} target="_blank" rel="noopener noreferrer">{app.name === "Jellyfin Web" ? "Web player help" : `${app.name} setup`}</Button>
          {#if app.name === "Jellyfin Web"}<Button href={webUrl || undefined} disabled={!usableAddress} target="_blank" rel="noopener noreferrer">Open web player</Button>{/if}</footer>
      </article>
    {/each}

    <article class="settings-card">
      <header><h3>Make music part of your library</h3></header>
      <div class="settings-row"><div><strong>Manual-only sources</strong><small>These appear in search and matching for you to choose. Automatic playback uses sources set to On.</small></div><Button variant="secondary" href="#/integrations/services">View sources</Button></div>
      <div class="settings-row"><div><strong>Hearts and playlists</strong><small>Favorite provider songs or add them to your playlists to find them in normal browsing. Your listening history alone does not add them to that saved view.</small></div></div>
      <div class="settings-row"><div><strong>Offline listening</strong><small>Download or cache a song in your music app to keep a copy on that device. Check it plays with the device offline before you travel.</small></div></div>
      <div class="settings-row"><div><strong>A quick first listen</strong><small>Play a song, seek well ahead, try a heart and playlist, and test an offline copy in your chosen app.</small></div></div>
    </article>
    <article class="settings-card">
      <header><h3>During the beta</h3></header>
      <div class="settings-row"><div><strong>Temporary source limits</strong><small>Some services can rate-limit requests or ask for verification. Choose another available source or wait a few minutes.</small></div></div>
      {#if context.backend === "Jellyfin"}<div class="settings-row"><div><strong>Finamp formats</strong><small>Some transcoded formats are unavailable. Include the song and app version when reporting a format error.</small></div></div>{/if}
      <div class="settings-row"><div><strong>Report a problem</strong><small>Use a song's report action to attach its details, or send a general note. Reports are saved for your administrator inside Allstarr.</small></div><Button href={reportHref()}>Report a problem</Button></div>
    </article>
  </section>
{/if}
