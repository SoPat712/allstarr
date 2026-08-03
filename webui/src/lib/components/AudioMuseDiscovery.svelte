<script lang="ts">
  import SelectField from "$lib/components/SelectField.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    intelligence,
    type AudioMuseAnalysis,
    type AudioMuseCluster,
    type AudioMuseMapPage,
    type AudioMuseTrack,
    type IntelligenceScope,
    type IntelligenceState,
  } from "$lib/api";

  let {
    scope,
    songs,
    onCreated = () => {},
  }: {
    scope: IntelligenceScope;
    songs: IntelligenceState["candidates"];
    onCreated?: () => void | Promise<void>;
  } = $props();

  let mode = $state("similar");
  let firstSong = $state("");
  let secondSong = $state("");
  let avoidSong = $state("");
  let query = $state("");
  let searchMode = $state<"text" | "lyrics">("text");
  let listeningPeriod = $state("90");
  let action = $state("");
  let error = $state("");
  let resultTitle = $state("");
  let results = $state<AudioMuseTrack[]>([]);
  let clusters = $state<AudioMuseCluster[]>([]);
  let clustersNext = $state<string | null>(null);
  let map = $state<AudioMuseMapPage | null>(null);
  let analysis = $state<AudioMuseAnalysis | null>(null);
  let playlistName = $state("Sound discoveries");
  let createOpen = $state(false);
  let creationKey = $state("");
  let createdMessage = $state("");

  const serverName = $derived(scope.protocol === "jellyfin" ? "Jellyfin" : scope.protocol === "subsonic" ? "Subsonic" : "your media server");
  const songOptions = $derived.by(() => {
    const seen = new Set<string>();
    return songs.filter((song) => song.trackKey && !seen.has(song.trackKey) && seen.add(song.trackKey))
      .map((song) => ({ value: song.trackKey, label: `${song.title || "Unknown song"}${song.artist ? ` — ${song.artist}` : ""}` }));
  });
  const resultSongs = $derived.by(() => {
    const source = map?.items ?? (clusters.length ? clusters.flatMap((group) => group.tracks) : results);
    const seen = new Set<string>();
    return source.filter((song) => song.trackId && !seen.has(song.trackId) && seen.add(song.trackId));
  });
  const resultCount = $derived(resultSongs.length);

  $effect(() => {
    if (!songOptions.length) return;
    if (!songOptions.some((item) => item.value === firstSong)) firstSong = songOptions[0].value;
    if (!songOptions.some((item) => item.value === secondSong)) secondSong = songOptions[1]?.value ?? songOptions[0].value;
    if (!songOptions.some((item) => item.value === avoidSong)) avoidSong = songOptions[1]?.value ?? songOptions[0].value;
  });

  $effect(() => {
    if (!analysis || !["queued", "running"].includes(analysis.state)) return;
    const timer = setTimeout(() => void pollAnalysis(), 1500);
    return () => clearTimeout(timer);
  });

  function message(cause: unknown) {
    const code = cause instanceof Error ? cause.message : "";
    if (code.includes("operation_unavailable")) return "AudioMuse does not support that choice yet.";
    if (code.includes("reconnect_or_scope_required")) return "Reconnect AudioMuse to this library and try again.";
    if (code.includes("preview_stale")) return "These songs changed. Find songs again before creating the playlist.";
    if (code.includes("not_selected")) return "Turn on AudioMuse for this library before creating the playlist.";
    if (code.includes("generated_playlist_invalid")) return "Check the playlist name and songs, then try again.";
    if (code.includes("request_invalid")) return "Check your song choices and try again.";
    return "AudioMuse could not complete this search. Try again in a moment.";
  }

  function clearResults(title: string) {
    resultTitle = title;
    results = [];
    clusters = [];
    clustersNext = null;
    map = null;
    createdMessage = "";
  }

  async function discover(name: string, title: string, operation: () => Promise<AudioMuseTrack[]>) {
    if (action) return;
    action = name;
    error = "";
    clearResults(title);
    try {
      results = await operation();
    } catch (cause) {
      error = message(cause);
    } finally {
      action = "";
    }
  }

  async function startAnalysis(rebuild = false) {
    if (action) return;
    action = "analysis";
    error = "";
    try {
      analysis = await intelligence.startAudioMuseAnalysis(scope, rebuild);
    } catch (cause) {
      error = message(cause);
    } finally {
      action = "";
    }
  }

  async function pollAnalysis() {
    if (!analysis) return;
    try {
      analysis = await intelligence.audioMuseAnalysis(scope, analysis.jobId);
    } catch (cause) {
      error = message(cause);
    }
  }

  async function loadClusters(cursor?: string) {
    if (action) return;
    action = cursor ? "clusters-more" : "clusters";
    error = "";
    if (!cursor) clearResults("Songs grouped by sound");
    try {
      const page = await intelligence.audioMuseClusters(scope, 10, cursor);
      clusters = cursor ? [...clusters, ...page.clusters] : page.clusters;
      clustersNext = page.nextCursor ?? null;
    } catch (cause) {
      error = message(cause);
    } finally {
      action = "";
    }
  }

  async function loadMap(cursor?: string) {
    if (action) return;
    action = cursor ? "map-more" : "map";
    error = "";
    if (!cursor) clearResults("Songs across your sound map");
    try {
      const page = await intelligence.audioMuseMap(scope, 50, cursor);
      if (cursor && map) {
        if (map.snapshotVersion && page.snapshotVersion && map.snapshotVersion !== page.snapshotVersion) {
          error = "The sound map changed. Load it again to see the latest songs.";
          return;
        }
        const seen = new Set(map.items.map((song) => song.trackId));
        map = {
          ...page,
          items: [...map.items, ...page.items.filter((song) => !seen.has(song.trackId))],
          isPartial: map.isPartial || page.isPartial,
        };
      } else map = page;
    } catch (cause) {
      error = message(cause);
    } finally {
      action = "";
    }
  }

  function confirmCreation() {
    creationKey = crypto.randomUUID();
    createOpen = true;
  }

  async function createPlaylist() {
    if (action || !resultSongs.length) return;
    action = "create";
    error = "";
    try {
      await intelligence.createAudioMusePlaylist(scope, playlistName.trim(),
        resultSongs.map((song) => song.trackId), creationKey);
      createOpen = false;
      createdMessage = `Allstarr is creating ${playlistName.trim()} in ${serverName}.`;
      await onCreated();
    } catch (cause) {
      error = message(cause);
    } finally {
      action = "";
    }
  }

  function analysisLabel(value: AudioMuseAnalysis) {
    if (value.state === "completed") return "Sound scan complete";
    if (value.state === "failed") return "Sound scan needs attention";
    if (value.state === "canceled") return "Sound scan stopped";
    return value.state === "queued" ? "Sound scan waiting" : "Scanning this library by sound";
  }
</script>

<section class="panel sound-discovery">
  <header>
    <div>
      <p class="eyebrow">AudioMuse</p>
      <h3>Explore by sound</h3>
      <p>Find songs already in this library. Allstarr will not create or change a {serverName} playlist unless you confirm below.</p>
    </div>
    <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void startAnalysis(analysis?.state === "completed")}>
      {action === "analysis" ? "Starting…" : analysis?.state === "completed" ? "Scan library again" : "Scan library sounds"}
    </button>
  </header>

  {#if analysis}
    <div class="scan-status" role="status">
      <span><strong>{analysisLabel(analysis)}</strong>{#if analysis.total}<small>{analysis.completed} of {analysis.total} songs</small>{/if}</span>
      {#if analysis.total}<progress max={analysis.total} value={analysis.completed}>{analysis.completed} of {analysis.total}</progress>{/if}
    </div>
  {/if}
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

  <div class="sound-controls">
    <label class="field"><span>How to explore</span><SelectField bind:value={mode} label="How to explore" options={[
      { value: "similar", label: "Find a similar sound" },
      { value: "path", label: "Connect two songs" },
      { value: "blend", label: "Include one sound and avoid another" },
      { value: "listening", label: "Use what I played most" },
      { value: "search", label: "Describe what you want" },
      { value: "library", label: "Browse the whole library by sound" },
    ]} /></label>

    {#if mode === "listening"}
      <form class="sound-form" onsubmit={(event) => { event.preventDefault(); void discover("listening", "Songs based on what you played", async () => (await intelligence.audioMuseFingerprint(scope, Number(listeningPeriod) as 30 | 90 | 365)).tracks); }}>
        <label class="field grow"><span>Listening period</span><SelectField bind:value={listeningPeriod} label="Listening period" options={[{ value: "30", label: "Past month" }, { value: "90", label: "Past 3 months" }, { value: "365", label: "Past year" }]} /></label>
        <button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "listening" ? "Finding…" : "Find songs"}</button>
      </form>
    {:else if mode === "search"}
      <form class="sound-form" onsubmit={(event) => { event.preventDefault(); void discover("search", searchMode === "lyrics" ? "Songs with matching words" : "Songs matching your description", async () => (await intelligence.audioMuseSearch(scope, query.trim(), searchMode)).tracks); }}>
        <label class="field grow"><span>{searchMode === "lyrics" ? "Words to find" : "Describe a sound"}</span><input bind:value={query} maxlength="500" placeholder={searchMode === "lyrics" ? "city lights in the rain" : "warm, quiet acoustic music"} required /></label>
        <label class="field"><span>Match</span><SelectField bind:value={searchMode} label="What to match" options={[{ value: "text", label: "The sound" }, { value: "lyrics", label: "Song lyrics" }]} /></label>
        <button class="button-primary" type="submit" disabled={Boolean(action) || !query.trim()}>{action === "search" ? "Searching…" : "Find songs"}</button>
      </form>
    {:else if mode === "library"}
      <div class="library-actions">
        <button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void loadClusters()}>{action === "clusters" ? "Grouping…" : "Group similar songs"}</button>
        <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void loadMap()}>{action === "map" ? "Loading…" : "List the sound map"}</button>
      </div>
    {:else if songOptions.length}
      <form class="sound-form" onsubmit={(event) => {
        event.preventDefault();
        if (mode === "similar") void discover("similar", "Songs with a similar sound", async () => (await intelligence.audioMuseSimilar(scope, [firstSong])).tracks);
        else if (mode === "path") void discover("path", "A sound path between your songs", async () => (await intelligence.audioMusePath(scope, firstSong, secondSong)).tracks);
        else void discover("blend", "Songs matching your choices", async () => (await intelligence.audioMuseBlend(scope, [firstSong], [avoidSong])).tracks);
      }}>
        <label class="field grow"><span>{mode === "path" ? "Starting song" : "Song to include"}</span><SelectField bind:value={firstSong} label={mode === "path" ? "Starting song" : "Song to include"} options={songOptions} /></label>
        {#if mode === "path"}
          <label class="field grow"><span>Ending song</span><SelectField bind:value={secondSong} label="Ending song" options={songOptions} /></label>
        {:else if mode === "blend"}
          <label class="field grow"><span>Sound to avoid</span><SelectField bind:value={avoidSong} label="Sound to avoid" options={songOptions} /></label>
        {/if}
        <button class="button-primary" type="submit" disabled={Boolean(action) || (mode === "path" && firstSong === secondSong) || (mode === "blend" && firstSong === avoidSong)}>{action ? "Finding…" : "Find songs"}</button>
      </form>
    {:else}
      <p class="credential-safety">Refresh recommendations first so you can choose a song from this library.</p>
    {/if}
  </div>

  {#if resultTitle}
    <section class="sound-results" aria-live="polite">
      <header><h4>{resultTitle}</h4><span>{resultCount} {resultCount === 1 ? "song" : "songs"}</span></header>
      {#if clusters.length}
        {#each clusters as group}
          <section class="sound-group"><h5>{group.name}</h5><ol>{#each group.tracks as song, index}<li><span>{index + 1}</span><div><strong>{song.title || "Unknown song"}</strong><small>{song.artist || "Unknown artist"}{song.album ? ` · ${song.album}` : ""}</small></div></li>{/each}</ol></section>
        {/each}
      {:else if map?.items.length}
        {#if map.isPartial}<p class="credential-safety">Some songs could not be shown because they are outside this library.</p>{/if}
        <ol>{#each map.items as song, index}<li><span>{index + 1}</span><div><strong>{song.title || "Unknown song"}</strong><small>{song.artist || "Unknown artist"}{song.album ? ` · ${song.album}` : ""}</small></div></li>{/each}</ol>
      {:else if results.length}
        <ol>{#each results as song, index}<li><span>{index + 1}</span><div><strong>{song.title || "Unknown song"}</strong><small>{song.artist || "Unknown artist"}{song.album ? ` · ${song.album}` : ""}</small>{#if song.explanation}<small>{song.explanation}</small>{/if}</div></li>{/each}</ol>
      {:else if !action}<div class="compact-empty"><strong>No matching songs</strong><p>Try a different song or description.</p></div>{/if}
      {#if clustersNext}<button class="button-secondary more-results" type="button" disabled={Boolean(action)} onclick={() => void loadClusters(clustersNext!)}>{action === "clusters-more" ? "Loading…" : "Show more groups"}</button>{/if}
      {#if map?.nextCursor}<button class="button-secondary more-results" type="button" disabled={Boolean(action)} onclick={() => void loadMap(map!.nextCursor!)}>{action === "map-more" ? "Loading…" : "Show more songs"}</button>{/if}
      {#if resultSongs.length}
        <form class="create-form" onsubmit={(event) => { event.preventDefault(); confirmCreation(); }}>
          <label class="field grow"><span>Playlist name</span><input bind:value={playlistName} maxlength="200" required /></label>
          <p>Allstarr will create <strong>{playlistName.trim() || "this playlist"}</strong> in {serverName} with {resultCount} {resultCount === 1 ? "song" : "songs"}.</p>
          <button class="button-primary" type="submit" disabled={Boolean(action)}>{`Create ${serverName} playlist`}</button>
        </form>
      {/if}
      {#if createdMessage}<p class="notice-success" role="status">{createdMessage}</p>{/if}
    </section>
  {/if}
</section>

<ConfirmDialog bind:open={createOpen}
  title={`Create ${playlistName.trim() || "this playlist"} in ${serverName}?`}
  description={`Allstarr will create ${playlistName.trim() || "this playlist"} in ${serverName} with ${resultCount} ${resultCount === 1 ? "song" : "songs"}.`}
  confirmLabel={action === "create" ? "Creating…" : `Create ${serverName} playlist`}
  cancelLabel="Do not create playlist" confirmClass="button-primary" disabled={Boolean(action)}
  onConfirm={createPlaylist} />

<style>
  .sound-discovery{display:grid;gap:1rem;padding:1.15rem}.sound-discovery>header,.sound-results>header{display:flex;align-items:start;justify-content:space-between;gap:1rem}.sound-discovery h3{margin:.2rem 0}.sound-discovery>header p:last-child{max-width:48rem;margin:0;color:var(--color-ink-muted)}.scan-status{display:grid;grid-template-columns:minmax(0,1fr) minmax(12rem,.5fr);align-items:center;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.scan-status small,.sound-results small{display:block;color:var(--color-ink-muted)}.scan-status progress{width:100%;accent-color:var(--color-signal)}.sound-controls{display:grid;grid-template-columns:minmax(13rem,.4fr) minmax(0,1.6fr);align-items:end;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.sound-form{display:flex;align-items:end;gap:.75rem}.sound-form .grow{flex:1}.library-actions{display:flex;gap:.75rem}.sound-results{display:grid;gap:.75rem;border-top:1px solid var(--color-edge);padding-top:1rem}.sound-results h4,.sound-results h5{margin:0}.sound-results ol{display:grid;margin:0;padding:0;list-style:none}.sound-results li{display:grid;grid-template-columns:2rem minmax(0,1fr);gap:.5rem;border-top:1px solid var(--color-edge);padding:.65rem 0}.sound-results li>span{color:var(--color-ink-muted);font-variant-numeric:tabular-nums}.sound-group{display:grid;gap:.5rem}.sound-group+ .sound-group{margin-top:.5rem}.more-results{justify-self:start}.create-form{display:grid;grid-template-columns:minmax(12rem,.7fr) minmax(14rem,1fr) auto;align-items:end;gap:.75rem;border-top:1px solid var(--color-edge);padding-top:1rem}.create-form p{margin:0;color:var(--color-ink-muted)}
  @media(max-width:900px){.sound-controls,.create-form{grid-template-columns:1fr}.sound-form{flex-wrap:wrap}.sound-form .grow{min-width:14rem}}
  @media(max-width:620px){.sound-discovery>header,.sound-form,.library-actions{align-items:stretch;flex-direction:column}.sound-discovery>header>button,.sound-form>button,.library-actions>button,.create-form>button{width:100%}.scan-status{grid-template-columns:1fr}.sound-form .grow{min-width:0}}
</style>
