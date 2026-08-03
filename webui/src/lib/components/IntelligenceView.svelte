<script lang="ts">
  import { X } from "lucide-svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import AudioMuseDiscovery from "$lib/components/AudioMuseDiscovery.svelte";
  import IntelligenceHistory from "$lib/components/IntelligenceHistory.svelte";
  import IntelligenceSchedules from "$lib/components/IntelligenceSchedules.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { home, intelligence, playlistLinks, type IntelligenceScope, type IntelligenceState, type MediaTarget } from "$lib/api";

  let protocol = $state("jellyfin");
  let backendInstanceId = $state("");
  let libraryScopeId = $state("");
  let data = $state<IntelligenceState | null>(null);
  let loading = $state(false);
  let action = $state("");
  let error = $state("");
  let purgeOpen = $state(false);
  let generatedName = $state("Your recommendations");
  let enabled = $state(false);
  let retentionDays = $state(30);
  let selectedSignals = $state<string[]>([]);
  let selectedProviders = $state<string[]>([]);
  let targetCredentialReferenceId = $state("");
  let mediaTargets = $state<MediaTarget[]>([]);
  let activeSection = $state("discover");
  let loadedScope = $state<IntelligenceScope | null>(null);

  const scope = $derived<IntelligenceScope>({ protocol, backendInstanceId, libraryScopeId });
  const activeScope = $derived(loadedScope ?? scope);
  const visibleCandidates = $derived(data?.candidates.filter((item) => !item.exclusions.length) ?? []);
  const audioMuseReady = $derived(data?.providers.some((item) => item.id === "audiomuse-ai" && item.enabled && item.available && item.state === "ready") ?? false);
  const runState = $derived(data?.actions.latestRunState?.replace("retryscheduled", "retry scheduled"));
  const runStatus = $derived(runState === "succeeded" ? "Ready" : ["pending", "running", "retry scheduled"].includes(runState ?? "") ? "Refreshing" : runState);
  const credentialOptions = $derived(mediaTargets
    .filter((item) => item.protocol === activeScope.protocol && item.backendInstanceId === activeScope.backendInstanceId &&
      (!item.libraryScopeId || item.libraryScopeId === activeScope.libraryScopeId) && item.credentialReferenceId)
    .map((item) => ({ value: item.credentialReferenceId!, label: item.displayName })));

  $effect(() => {
    if (!["pending", "running", "retry scheduled"].includes(runState ?? "")) return;
    const timer = setTimeout(() => void refresh(), 1500);
    return () => clearTimeout(timer);
  });

  function adopt(next: IntelligenceState) {
    data = next;
    enabled = Boolean(next.policy?.enabled);
    retentionDays = next.policy?.retentionDays ?? 30;
    selectedSignals = next.availableSignalTypes.filter((item) => item.enabled).map((item) => item.id);
    selectedProviders = next.providers.filter((item) => item.enabled).map((item) => item.id);
    targetCredentialReferenceId = next.policy?.targetCredentialReferenceId ?? "";
  }

  async function load() {
    if (!backendInstanceId.trim() || !libraryScopeId.trim()) return;
    const requestedScope = { ...scope };
    loading = true;
    error = "";
    try {
      const [next, targets] = await Promise.all([
        intelligence.get(requestedScope),
        playlistLinks.targets().catch(() => ({ targets: [] })),
      ]);
      mediaTargets = targets.targets;
      loadedScope = requestedScope;
      adopt(next);
    } catch (cause) {
      data = null;
      loadedScope = null;
      error = cause instanceof Error ? cause.message : "Intelligence could not be loaded.";
    } finally {
      loading = false;
    }
  }

  async function perform(name: string, operation: () => Promise<unknown>) {
    if (action) return;
    action = name;
    error = "";
    try {
      await operation();
      adopt(await intelligence.get(activeScope));
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The action could not be completed.";
    } finally {
      action = "";
    }
  }

  async function refresh() {
    if (!loadedScope) return;
    try {
      adopt(await intelligence.get(loadedScope));
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Intelligence could not be refreshed.";
    }
  }

  function toggle(values: string[], value: string, checked: boolean) {
    return checked ? [...new Set([...values, value])] : values.filter((item) => item !== value);
  }

  function generatedStatus(item: IntelligenceState["generatedSets"][number]) {
    const server = activeScope.protocol === "jellyfin" ? "Jellyfin" : activeScope.protocol === "subsonic" ? "Subsonic" : "the media server";
    if (item.materialized) return `Created in ${server}`;
    if (["pending", "running"].includes(item.state)) return `Creating in ${server}`;
    if (item.state === "failed") return `Not created in ${server}`;
    return "Saved in Allstarr";
  }

  function providerStatus(state: string) {
    if (state === "ready") return "Ready";
    if (["unsupported", "unavailable"].includes(state)) return "Unavailable";
    if (["unauthorized", "degraded", "failed"].includes(state)) return "Needs attention";
    return state.replaceAll("_", " ");
  }
</script>

<section class="intelligence-view">
  <header class="route-heading">
    <div>
      <p class="eyebrow">Private discovery</p>
      <h2>Listen deeper, without exposing your history.</h2>
      <p>Recommendations, listening history, and generated playlists stay private to this account and music library.</p>
    </div>
    {#if data?.actions.canRun && activeSection === "discover"}
      <div class="heading-actions">
        {#if runStatus}<span class={`status-pill ${runState === "succeeded" ? "healthy" : "suggested"}`}>{runStatus}</span>{/if}
        <button class="button-primary" type="button" disabled={Boolean(action)}
          onclick={() => void perform("run", () => intelligence.run(activeScope))}>
          {action === "run" ? "Starting…" : "Refresh recommendations"}
        </button>
      </div>
    {/if}
  </header>

  <form class="panel scope-card" onsubmit={(event) => { event.preventDefault(); void load(); }}>
    <label class="field"><span>Media server</span><SelectField bind:value={protocol} label="Media server" options={[{ value: "jellyfin", label: "Jellyfin" }, { value: "subsonic", label: "Subsonic" }]} /></label>
    <label class="field"><span>Server connection</span><input bind:value={backendInstanceId} maxlength="200" placeholder="main" required /></label>
    <label class="field"><span>Music library</span><input bind:value={libraryScopeId} maxlength="300" placeholder="music" required /></label>
    <button class="button-secondary" type="submit" disabled={loading}>{loading ? "Loading…" : "Open library"}</button>
  </form>

  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

  {#if loading}
    <div class="intelligence-grid" aria-busy="true" aria-label="Loading Intelligence">
      {#each Array(3) as _}<div class="panel skeleton-panel"></div>{/each}
    </div>
  {:else if !data}
    <section class="panel empty-state">
      <span class="empty-orbit" aria-hidden="true">✦</span>
      <p class="eyebrow">Choose a library</p>
      <h2>Choose a music library.</h2>
      <p>Enter the connection and library names configured for your media server.</p>
    </section>
  {:else if data.state === "unauthorized" || data.state === "error"}
    <RouteError
      eyebrow={data.state}
      title="This library is unavailable."
      message={data.message ?? "The library could not be loaded."}
      onRetry={load}
    />
  {:else}
    {#if data.state === "degraded"}
      <div class="degraded-banner" role="status"><span aria-hidden="true">!</span><p><strong>Some discovery sources need attention.</strong> Existing results remain available.</p></div>
    {/if}
    {#if activeSection === "discover" && data.actions.progress}
      <section class="panel run-progress" role="status">
        <div><p class="eyebrow">Refreshing recommendations</p><strong>{data.actions.progress.message}</strong>
          {#if data.actions.progress.provider || data.actions.progress.playlist || data.actions.progress.track}<small>{[data.actions.progress.provider, data.actions.progress.playlist, data.actions.progress.track].filter(Boolean).join(" · ")}</small>{/if}
          {#if data.actions.attemptCount && data.actions.maxAttempts}<small>Attempt {data.actions.attemptCount} of {data.actions.maxAttempts}{data.actions.failureCount ? ` · ${data.actions.failureCount} failed` : ""}</small>{/if}
        </div>
        {#if data.actions.progress.total}<progress max={data.actions.progress.total} value={data.actions.progress.completed ?? 0}>{data.actions.progress.completed ?? 0} / {data.actions.progress.total}</progress>{/if}
        {#if data.actions.canCancel && data.actions.latestJobId}<button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void perform("cancel", () => home.cancelJob(data!.actions.latestJobId!))}>{action === "cancel" ? "Cancelling…" : "Cancel refresh"}</button>{/if}
      </section>
    {/if}
    <SegmentedNav items={[
      { id: "discover", label: "Discover" },
      { id: "history", label: "Listening history" },
      { id: "settings", label: "Settings" },
    ]} active={activeSection} label="Intelligence sections" class="intelligence-tabs" onchange={(value) => activeSection = value} />

    {#if activeSection === "history"}
      <IntelligenceHistory scope={activeScope} />
    {:else if activeSection === "settings"}
      <div class="settings-stack">
        <section class="panel privacy-card">
          <header><div><p class="eyebrow">Control</p><h3>Privacy and sources</h3></div><span class={`status-pill ${enabled ? "healthy" : "suggested"}`}>{enabled ? "Enabled" : "Off"}</span></header>
          <form onsubmit={(event) => {
            event.preventDefault();
            void perform("policy", () => intelligence.savePolicy(activeScope, {
              enabled, retentionDays, allowedSignalTypes: selectedSignals,
              enabledProviders: selectedProviders,
              targetCredentialReferenceId: targetCredentialReferenceId || null,
              expectedRevision: data!.policy?.revision ?? 0,
            }));
          }}>
            <label class="toggle-line"><input type="checkbox" bind:checked={enabled} /><span><strong>Use my listening signals</strong><small>Nothing is retained until this is enabled.</small></span></label>
            <label class="field"><span>Retention</span><SelectField value={String(retentionDays)} label="Retention" onchange={(value) => { retentionDays = Number(value); }} options={[
              { value: "7", label: "7 days" }, { value: "30", label: "30 days" },
              { value: "90", label: "90 days" }, { value: "365", label: "1 year" },
            ]} /></label>
            {#if credentialOptions.length}
              <label class="field"><span>Where generated playlists are created</span><SelectField bind:value={targetCredentialReferenceId} label="Generated playlist destination" options={[{ value: "", label: "Use the connected media server" }, ...credentialOptions]} /></label>
            {/if}
            <fieldset><legend>Signals</legend>{#each data.availableSignalTypes as item}<label><input type="checkbox" checked={selectedSignals.includes(item.id)} onchange={(event) => selectedSignals = toggle(selectedSignals, item.id, event.currentTarget.checked)} /> {item.label}</label>{/each}</fieldset>
            <fieldset><legend>Sources</legend>{#each data.providers as provider}<label class:unavailable={!provider.available}><input type="checkbox" disabled={!provider.available} checked={selectedProviders.includes(provider.id)} onchange={(event) => selectedProviders = toggle(selectedProviders, provider.id, event.currentTarget.checked)} /> <span><strong>{provider.label}</strong><small>{provider.description} · {providerStatus(provider.state)}</small></span></label>{/each}</fieldset>
            <footer><button class="button-danger" type="button" onclick={() => purgeOpen = true}>Turn off and clear</button><button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "policy" ? "Saving…" : "Save settings"}</button></footer>
          </form>
        </section>
        <IntelligenceSchedules scope={activeScope} schedules={data.schedules ?? []} policyEnabled={enabled} onChanged={refresh} />
      </div>
    {:else}
      {#if audioMuseReady}<AudioMuseDiscovery scope={activeScope} songs={visibleCandidates} onCreated={refresh} />{/if}
      <div class="intelligence-grid">
        <section class="panel recommendations">
          <header><div><p class="eyebrow">For you</p><h3>Recommendations</h3></div><span>{visibleCandidates.length} tracks</span></header>
          {#if visibleCandidates.length}
            <ol class="recommendation-list">
              {#each visibleCandidates as item}
                <li>
                  <span class="track-art">{#if item.artworkUrl}<img src={item.artworkUrl} alt="" loading="lazy" />{:else}<span aria-hidden="true">♪</span>{/if}</span>
                  <div class="track-copy"><strong>{item.title || item.trackKey}</strong><small>{item.artist || item.providerId}{item.album ? ` · ${item.album}` : ""}</small><details><summary>Why this track</summary><ul>{#each item.explanations as reason}<li>{reason.explanation}</li>{/each}</ul></details></div>
                  <div class="track-actions"><span class="score">{Math.round(item.score * 100)}%</span><button type="button" disabled={Boolean(action)} onclick={() => void perform(`similar:${item.id}`, () => intelligence.run(activeScope, [item.trackKey]))}>Similar</button><button type="button" aria-label={`Dismiss ${item.title || item.trackKey}`} disabled={Boolean(action)} onclick={() => void perform(`dismiss:${item.id}`, () => intelligence.feedback(activeScope, item.id, "dismiss", item.feedback?.revision ?? 0))}><X size={16} aria-hidden="true" /></button></div>
                </li>
              {/each}
            </ol>
          {:else}<div class="compact-empty"><strong>No recommendations yet</strong><p>Enable at least one ready source, then run a refresh.</p></div>{/if}
        </section>

        <aside class="side-stack">
          <section class="panel profile-card"><p class="eyebrow">Recent listening</p><h3>Your profile</h3>{#if data.visualization.length}{#each data.visualization as item}<label><span>{item.label}</span><meter min="0" max="1" value={item.value}>{item.value}</meter></label>{/each}{:else}<p class="muted">No retained listening signals yet.</p>{/if}</section>
          <section class="panel generated-card"><p class="eyebrow">Saved output</p><h3>Generated playlists</h3>{#each data.generatedSets as item}<div class="generated-row"><span><strong>{item.name}</strong><small>{item.trackCount} tracks</small></span><span class={`status-pill ${item.materialized ? "healthy" : "suggested"}`}>{generatedStatus(item)}</span></div>{:else}<p class="muted">No generated playlists yet.</p>{/each}{#if data.actions.canGenerate && data.actions.latestRunId}<form class="generate-form" onsubmit={(event) => { event.preventDefault(); void perform("generate", () => intelligence.generate(activeScope, data!.actions.latestRunId!, generatedName)); }}><label class="field"><span>Playlist name</span><input bind:value={generatedName} maxlength="200" required /></label><button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "generate" ? "Creating…" : "Create playlist"}</button></form>{/if}</section>
        </aside>
      </div>
    {/if}
  {/if}
</section>

<ConfirmDialog
  bind:open={purgeOpen}
  title="Clear this library’s Intelligence data?"
  description="Listening signals, recommendations, feedback, and saved playlists for this account and music library will be removed."
  confirmLabel="Turn off and clear"
  cancelLabel="Keep my data"
  onConfirm={() => perform("purge", () => intelligence.purge(activeScope))}
/>

<style>
  .intelligence-view{min-width:0}
  .intelligence-view{display:grid;gap:1.25rem}.route-heading{display:flex;align-items:end;justify-content:space-between;gap:1rem}.route-heading h2{margin:.25rem 0;font-family:var(--font-display);font-size:clamp(1.5rem,3vw,2.2rem)}.route-heading p:last-child{color:var(--color-ink-muted)}.heading-actions{display:flex;align-items:center;gap:.75rem}.scope-card{display:grid;grid-template-columns:repeat(3,minmax(0,1fr)) auto;align-items:end;gap:1rem;padding:1rem}.run-progress{display:grid;grid-template-columns:minmax(0,1fr) minmax(10rem,.5fr) auto;align-items:center;gap:1rem;padding:1rem}.run-progress p{margin:0}.run-progress small{display:block;color:var(--color-ink-muted)}.run-progress progress{width:100%;accent-color:var(--color-signal)}.settings-stack{display:grid;gap:1rem}.intelligence-grid{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(18rem,.8fr);gap:1rem}.recommendations,.profile-card,.generated-card,.privacy-card{padding:1.15rem}.recommendations>header,.privacy-card>header{display:flex;align-items:center;justify-content:space-between}.recommendations h3,.profile-card h3,.generated-card h3,.privacy-card h3{margin:.2rem 0 1rem}.recommendation-list{display:grid;margin:0;padding:0;list-style:none}.recommendation-list>li{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:.85rem;align-items:center;border-top:1px solid var(--color-edge);padding:.9rem 0}.track-copy small,summary{color:var(--color-ink-muted);font-size:.75rem}.track-copy details{margin-top:.35rem}.track-copy ul{margin:.4rem 0 0;padding-left:1.1rem;color:var(--color-ink-muted);font-size:.78rem}.side-stack{display:grid;align-content:start;gap:1rem}.profile-card label,.generated-row{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.7rem 0}.profile-card meter{width:55%;accent-color:var(--color-signal)}.generated-row span:first-child strong,.generated-row span:first-child small{display:block}.generated-row small{color:var(--color-ink-muted)}.generate-form{display:grid;gap:.75rem;margin-top:1rem}.privacy-card form{display:grid;grid-template-columns:minmax(14rem,.6fr) 1fr 1fr;gap:1rem}.toggle-line{display:flex;gap:.75rem}.toggle-line span>*,fieldset label span>*{display:block}.toggle-line small,fieldset small{color:var(--color-ink-muted)}fieldset{display:grid;align-content:start;gap:.55rem;border:0;margin:0;padding:0}fieldset legend{margin-bottom:.55rem;font-weight:750}fieldset label{display:flex;gap:.5rem}.privacy-card footer{grid-column:1/-1;display:flex;justify-content:space-between;gap:.75rem;border-top:1px solid var(--color-edge);padding-top:1rem}
  @media(max-width:900px){.scope-card{grid-template-columns:1fr 1fr}.run-progress{grid-template-columns:1fr}.intelligence-grid{grid-template-columns:1fr}.privacy-card form{grid-template-columns:1fr 1fr}}
  @media(max-width:620px){.route-heading{align-items:stretch;flex-direction:column}.scope-card,.privacy-card form,.run-progress{grid-template-columns:1fr}.recommendation-list>li{grid-template-columns:auto minmax(0,1fr)}.track-actions{grid-column:2;flex-wrap:wrap}.privacy-card footer{grid-column:auto;flex-direction:column-reverse}.privacy-card footer>*{width:100%}}
</style>
