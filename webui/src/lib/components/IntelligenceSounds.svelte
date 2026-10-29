<script lang="ts">
  import { onMount, tick } from "svelte";
  import { audioMuse, ApiError, type AudioMuseConnection, type AudioMuseStatus, type AudioMuseSong, type AudioMusePreview } from "$lib/api";
  import { formatCount } from "$lib/intelligence";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import SearchField from "$lib/components/SearchField.svelte";

  let { administrator, status, onrefresh }: { administrator: boolean; status: AudioMuseStatus | null; onrefresh: () => Promise<void> } = $props();
  let connection = $state<AudioMuseConnection | null>(null), connectionOpen = $state(false), connectionLoading = $state(false);
  let address = $state(""), key = $state(""), connectionError = $state(""), action = $state("");
  let query = $state(""), seeds = $state<AudioMuseSong[]>([]), seed = $state<AudioMuseSong | null>(null);
  let searching = $state(false), searched = $state(false), searchError = $state("");
  let result = $state<AudioMusePreview | null>(null), resultTitle = $state(""), resultKind = $state(""), playlistName = $state(""), previewError = $state("");
  let searchRequest = 0, connectionRequest = 0, disposed = false;
  const connected = $derived(status?.reason === null);
  const count = $derived(`${status?.coverageComplete ? "" : "At least "}${formatCount(status?.analyzedSongs ?? 0)}`);
  const statusLabel = $derived(!status ? "Checking connection…" : connected ? `Connected · ${count} songs analyzed` : message(status.reason));
  const moods = [{ id: "calm", name: "Calm", description: "Relaxed, lower-energy songs" }, { id: "workout", name: "Workout", description: "Danceable, higher-energy songs" }] as const;

  function message(reason: string | null) {
    if (reason === "not_enabled" || reason === "not_set_up") return "Not set up";
    if (reason === "song_not_analyzed") return "AudioMuse hasn't analyzed this song yet";
    if (reason === "too_little_analyzed_music") return "Not enough analyzed songs in your library yet";
    return "Can't reach AudioMuse";
  }
  async function editConnection() {
    const current = ++connectionRequest;
    connectionOpen = true; connectionLoading = true; connectionError = ""; key = "";
    connection = null;
    try {
      const next = await audioMuse.connection();
      if (!disposed && current === connectionRequest) { connection = next; address = next.baseUrl; }
    } catch { if (!disposed && current === connectionRequest) connectionError = "The connection could not be loaded. Close and try again."; }
    finally { if (!disposed && current === connectionRequest) connectionLoading = false; }
  }
  async function configure() {
    if (action || !connection) return;
    action = "connection"; connectionError = "";
    try {
      const next = await audioMuse.configure(address.trim(), key, connection.revision);
      if (disposed) return;
      connection = next; key = ""; connectionOpen = false;
      await onrefresh();
    } catch (cause) {
      if (!disposed) connectionError = cause instanceof ApiError && cause.status === 409
        ? "The connection changed. Close and reopen to load it before saving."
        : "The connection could not be saved. Check the address and try again.";
    } finally { if (!disposed) action = ""; }
  }
  $effect(() => {
    const value = query.trim(), current = ++searchRequest;
    seed = null; seeds = []; searchError = ""; searched = false; searching = Boolean(value);
    if (!value) return;
    const timer = setTimeout(async () => {
      try {
        const next = await audioMuse.seeds(value);
        if (current !== searchRequest || disposed) return;
        seeds = next.tracks;
        if (next.reason) searchError = message(next.reason);
      } catch { if (current === searchRequest && !disposed) searchError = "Songs could not be found. Try again."; }
      finally { if (current === searchRequest && !disposed) { searching = false; searched = true; } }
    }, 300);
    return () => clearTimeout(timer);
  });
  async function preview(mood?: "calm" | "workout") {
    if (action || !connected || !mood && !seed) return;
    action = "preview"; previewError = "";
    const title = mood ? moods.find(item => item.id === mood)!.name : seed!.title;
    try {
      const next = mood ? await audioMuse.mood(mood) : await audioMuse.similar(seed!.id);
      if (disposed) return;
      result = next; resultKind = mood ?? "similar";
      resultTitle = mood ? `${title} playlist` : `Songs that sound like ${title}`;
      playlistName = (mood ? title : `Sounds like ${title}`).slice(0, 200);
      if (next.reason) previewError = message(next.reason);
      else { await tick(); document.getElementById("preview-heading")?.scrollIntoView({ behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "start" }); }
    } catch { if (!disposed) { result = null; previewError = "The preview is unavailable. Try again."; } }
    finally { if (!disposed) action = ""; }
  }
  async function save() {
    if (action || !result?.previewId || !playlistName.trim()) return;
    action = "save";
    try {
      const saved = await audioMuse.save(result.previewId, playlistName.trim());
      if (disposed) return;
      if (saved.reason || !saved.playlistId) { toast.error("The playlist could not be saved. Preview it again and retry."); return; }
      toast.success("Saved to your playlists", { label: "Open", run: () => { window.location.hash = "#/library/playlists"; } });
    } catch { if (!disposed) toast.error("The playlist could not be saved. Preview it again and retry."); }
    finally { if (!disposed) action = ""; }
  }
  onMount(() => {
    const current = ++connectionRequest;
    if (administrator) void audioMuse.connection().then(value => { if (!disposed && current === connectionRequest) connection = value; }).catch(() => {});
    return () => { disposed = true; searchRequest++; };
  });
</script>

<div class="sounds">
  {#if administrator}
    <section class="settings-card" aria-label="AudioMuse connection">
      <header><h2>AudioMuse connection</h2></header>
      <div class="settings-row"><div><strong>{statusLabel}</strong>{#if connection?.keyConfigured}<small>Key saved</small>{/if}</div><Button variant="secondary" disabled={Boolean(action)} onclick={editConnection}>Edit connection</Button></div>
    </section>
  {/if}
  <div class="section-heading"><p>Find familiar sounds and music for your mood.</p><RefreshButton variant="secondary" label="Refresh sounds" disabled={Boolean(action)} onrefresh={onrefresh} /></div>
  {#if !status}<Skeleton class="skeleton-panel" aria-label="Loading sounds" />
  {:else}
    {#if !connected}<p class="notice-info">{message(status.reason)}. Sounds will be available when AudioMuse is ready.</p>{/if}
    <section class="settings-card discovery" aria-labelledby="similar-heading">
      <h2 id="similar-heading">Sounds like this</h2>
      <SearchField class="field" bind:value={query} label="Find a song from your library" placeholder="Song title or artist" />
      {#if searching}<p role="status">Finding songs…</p>{/if}
      {#if searchError}<p role="status">{searchError}</p>{/if}
      {#if !seed && seeds.length}<ul class="seed-list" aria-label="Songs from your library">{#each seeds as song (song.id)}
        <li><button type="button" class="seed-choice" aria-label={`${song.title} by ${song.artist}`} onclick={() => seed = song}><strong>{song.title}</strong><span>{song.artist}{song.album ? ` · ${song.album}` : ""}</span></button></li>
      {/each}</ul>{:else if searched && !searching && !seed && !seeds.length && !searchError}<p>No analyzed songs found. Try another song.</p>{/if}
      <div class="section-heading">{#if seed}<p><strong>{seed.title}</strong> · {seed.artist}</p>{/if}<Button variant="secondary" disabled={!seed || !connected || Boolean(action)} onclick={() => preview()}>Preview</Button></div>
    </section>
    <section aria-labelledby="moods-heading"><h2 id="moods-heading">Mood playlists</h2><div class="mood-cards">
      {#each moods as mood}<article class="settings-card discovery" aria-label={mood.name}><h3>{mood.name}</h3><p>{mood.description}</p><Button variant="secondary" disabled={!connected || Boolean(action)} onclick={() => preview(mood.id)}>Preview</Button></article>{/each}
    </div></section>
    {#if action === "preview"}<p role="status">Preparing your preview…</p>{/if}
    {#if previewError}<p class="notice-info" role="status">{previewError}</p>{/if}
    {#if result && !result.reason && result.tracks.length}
      <section class="settings-card preview" aria-label="Playlist preview" aria-busy={action === "preview"}>
        <h2 id="preview-heading">{resultTitle}</h2>
        {#if resultKind === "similar" && result.tracks.length < 25}<p>Only {result.tracks.length} similar songs in your library</p>
        {:else if resultKind !== "similar"}<p>From {status.coverageComplete ? "" : "at least "}{formatCount(status.analyzedSongs)} analyzed songs</p>{/if}
        <ol>{#each result.tracks.slice(0, 25) as song (song.id)}<li><div><strong>{song.title}</strong><span>{song.artist}</span>{#if song.album}<small>{song.album}</small>{/if}</div></li>{/each}</ol>
        <form class="save-form" onsubmit={event => { event.preventDefault(); void save(); }}><label class="field"><span>Playlist name</span><input bind:value={playlistName} maxlength="200" required /></label><Button type="submit" disabled={!result.previewId || !playlistName.trim() || Boolean(action)}>Save as playlist</Button></form>
      </section>
    {/if}
    <footer>AudioMuse only knows songs in your library.</footer>
  {/if}
</div>

<Dialog.Root bind:open={connectionOpen} onOpenChange={open => { if (!open) key = ""; }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog">
    <header><div><Dialog.Title>AudioMuse connection</Dialog.Title><Dialog.Description>Connect AudioMuse to find sounds in your library.</Dialog.Description></div></header>
    <form onsubmit={event => { event.preventDefault(); void configure(); }}>
      <div class="connection-fields">
        {#if connectionLoading}<p role="status">Loading connection…</p>{/if}
        <label class="field"><span>AudioMuse address</span><input type="url" bind:value={address} maxlength="2048" disabled={connectionLoading || Boolean(action)} /></label>
        <label class="field"><span>API key</span><input type="password" aria-label="API key" aria-describedby="sounds-key-help" bind:value={key} autocomplete="new-password" maxlength="4096" disabled={connectionLoading || Boolean(action)} /><small id="sounds-key-help">{connection?.keyConfigured ? "Key saved. Leave blank to keep it." : "Enter your AudioMuse key."}</small></label>
        {#if connectionError}<p class="notice-error" role="alert">{connectionError}</p>{/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</Dialog.Close><Button type="submit" disabled={connectionLoading || Boolean(action) || !connection}>Save</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<style>
  .sounds { display: grid; gap: var(--space-6); min-width: 0; }
  .section-heading, .save-form { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .section-heading p, article p, footer, .preview > p { color: var(--color-ink-muted); }
  h2 { font-size: 1.125rem; font-weight: 600; overflow-wrap: anywhere; }
  h3, .seed-choice strong { font-weight: 600; }
  #moods-heading { margin-bottom: var(--space-4); }
  .discovery, .preview { display: grid; gap: var(--space-4); padding: var(--surface-gutter); min-width: 0; }
  .discovery :global(.search-field input) { width: 100%; min-width: 0; }
  .mood-cards { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--section-gap); }
  .mood-cards :global(button) { justify-self: start; }
  .seed-list, ol { list-style: none; margin: 0; padding: 0; }
  .seed-choice { display: grid; width: 100%; min-height: 64px; text-align: left; padding: var(--space-3); border-radius: var(--radius-md); color: var(--color-ink); cursor: pointer; }
  .seed-choice:hover { background: var(--color-panel-raised); }
  .seed-choice:focus-visible { outline: 2px solid var(--color-primary); }
  .seed-choice span, li span, li small { color: var(--color-ink-muted); font-size: var(--text-sm); }
  li + li { border-top: 1px solid var(--color-edge); }
  ol li { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); padding-block: var(--space-3); }
  ol li div { min-width: 0; }
  li strong, li span, li small { display: block; overflow-wrap: anywhere; }
  .save-form { align-items: end; }
  .save-form .field { flex: 1; min-width: min(100%, 12rem); }
  .connection-fields { display: grid; gap: var(--space-4); min-width: 0; }
  footer { font-size: var(--text-sm); }
  @media (max-width: 600px) { .mood-cards { grid-template-columns: 1fr; } }
</style>
