<script lang="ts">
  import { onMount } from "svelte";
  import { X } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import AudioMuseDiscovery from "$lib/components/AudioMuseDiscovery.svelte";
  import IntelligenceHistory from "$lib/components/IntelligenceHistory.svelte";
  import ListeningAppsCard from "$lib/components/ListeningAppsCard.svelte";
  import IntelligenceSchedules from "$lib/components/IntelligenceSchedules.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { home, intelligence, playlistLinks, type IntelligenceScope, type IntelligenceState, type MediaTarget } from "$lib/api";

  type IntelligenceSection = "overview" | "history" | "imports" | "discover" | "automation";

  let { initialSection = "overview" }: { initialSection?: string } = $props();
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
  let selectedTargetId = $state("");
  let targetsLoading = $state(true);
  let loadedScope = $state<IntelligenceScope | null>(null);

  const activeSection = $derived<IntelligenceSection>(
    initialSection === "history" || initialSection === "imports" ? initialSection
      : initialSection === "discover" || initialSection === "recommendations" ? "discover"
        : initialSection === "automation" || initialSection === "settings" ? "automation"
          : "overview",
  );
  const sectionIntro = $derived({
    overview: { title: "Your listening, explained.", description: "See what you play, what Allstarr learned, and what is ready for discovery." },
    history: { title: "Your listening history.", description: "Search, correct, and export the activity saved for this account and library." },
    imports: { title: "Bring your history with you.", description: "Upload Spotify Extended Streaming History or exports from your other listening services." },
    discover: { title: "Turn listening into discovery.", description: "Review recommendations and create playlists without sending your history to Allstarr." },
    automation: { title: "Choose what Allstarr remembers.", description: "Control private history, recommendation inputs, listening services, and schedules." },
  }[activeSection]);
  const historySection = $derived(activeSection === "imports" ? "imports" : activeSection === "history" ? "history" : "overview");
  const scope = $derived<IntelligenceScope>({ protocol, backendInstanceId, libraryScopeId });
  const activeScope = $derived(loadedScope ?? scope);
  const visibleCandidates = $derived(data?.candidates.filter((item) => !item.exclusions.length) ?? []);
  const audioMuseReady = $derived(data?.providers.some((item) => item.id === "audiomuse-ai" && item.enabled && item.available && item.state === "ready") ?? false);
  const runState = $derived(data?.actions.latestRunState?.replace("retryscheduled", "retry scheduled"));
  const runStatus = $derived(runState === "succeeded" ? "Ready" : ["pending", "running", "retry scheduled"].includes(runState ?? "") ? "Refreshing" : runState);
  const readyRecommendationSources = $derived(data?.providers.filter((item) => item.enabled && item.available && item.state === "ready").length ?? 0);
  const scopedTargets = $derived(mediaTargets.filter((item) => Boolean(item.libraryScopeId)));
  const selectedTarget = $derived(scopedTargets.find((item) => item.id === selectedTargetId) ?? scopedTargets[0]);
  const targetOptions = $derived(scopedTargets.map((item) => ({ value: item.id, label: targetLabel(item) })));
  const credentialOptions = $derived(mediaTargets
    .filter((item) => item.protocol === activeScope.protocol && item.backendInstanceId === activeScope.backendInstanceId &&
      (!item.libraryScopeId || item.libraryScopeId === activeScope.libraryScopeId) && item.credentialReferenceId)
    .map((item) => ({ value: item.credentialReferenceId!, label: item.displayName })));
  const nextSchedule = $derived(data?.schedules.filter((item) => item.enabled && item.nextRunAt)
    .sort((left, right) => new Date(left.nextRunAt!).getTime() - new Date(right.nextRunAt!).getTime())[0]);

  $effect(() => {
    if (!["pending", "running", "retry scheduled"].includes(runState ?? "")) return;
    const timer = setTimeout(() => void refresh(), 1500);
    return () => clearTimeout(timer);
  });

  onMount(() => { void discoverTargets(); });

  function adopt(next: IntelligenceState) {
    data = next;
    enabled = Boolean(next.policy?.enabled);
    retentionDays = next.policy?.retentionDays ?? 30;
    selectedSignals = next.availableSignalTypes.filter((item) => item.enabled).map((item) => item.id);
    selectedProviders = next.providers.filter((item) => item.enabled).map((item) => item.id);
    targetCredentialReferenceId = next.policy?.targetCredentialReferenceId ?? "";
  }

  function serverLabel(value: string) {
    return value === "jellyfin" ? "Jellyfin" : value === "subsonic" ? "Subsonic" : "Media server";
  }

  function libraryLabel(value: string) {
    return value.replaceAll("-", " ").replaceAll("_", " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
  }

  function targetLabel(target: MediaTarget) {
    return `${libraryLabel(target.libraryScopeId ?? "Music")} · ${serverLabel(target.protocol)}`;
  }

  async function discoverTargets() {
    targetsLoading = true;
    error = "";
    try {
      const response = await playlistLinks.targets();
      mediaTargets = response.targets;
      const target = response.targets.find((item) => Boolean(item.libraryScopeId));
      if (target) {
        targetsLoading = false;
        await openTarget(target.id);
      }
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Your music libraries could not be found.";
    } finally {
      targetsLoading = false;
    }
  }

  async function openTarget(targetId: string) {
    const target = mediaTargets.find((item) => item.id === targetId && item.libraryScopeId);
    if (!target?.libraryScopeId) return;
    selectedTargetId = target.id;
    protocol = target.protocol;
    backendInstanceId = target.backendInstanceId;
    libraryScopeId = target.libraryScopeId;
    await load({ protocol: target.protocol, backendInstanceId: target.backendInstanceId, libraryScopeId: target.libraryScopeId });
  }

  async function load(requestedScope: IntelligenceScope = { ...scope }) {
    if (!requestedScope.backendInstanceId.trim() || !requestedScope.libraryScopeId.trim()) return;
    loading = true;
    error = "";
    try {
      const next = await intelligence.get(requestedScope);
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

  function listeningServiceMessage(service: NonNullable<IntelligenceState["listeningServices"]>[number]) {
    if (!service.configured) return `Allstarr will not send listens to ${service.label}.`;
    if (service.requiresReauthentication) return `Reconnect ${service.label} before Allstarr can send more listens.`;
    if (service.latestState === "delivered") return `Allstarr sent the latest completed listen to ${service.label}.`;
    if (service.latestState === "ignored") return `${service.label} did not need the latest completed listen.`;
    if (service.latestState === "retrying") return `Allstarr will retry sending the latest completed listen to ${service.label}.`;
    if (service.latestState === "permanentfailure") return `${service.label} rejected the latest completed listen.`;
    return `Allstarr will send completed listens to ${service.label}.`;
  }

  function listeningServiceStatus(service: NonNullable<IntelligenceState["listeningServices"]>[number]) {
    if (!service.configured) return { label: "Off", class: "suggested" };
    if (service.requiresReauthentication || service.latestState === "permanentfailure")
      return { label: "Needs attention", class: "suggested" };
    if (service.latestState === "retrying") return { label: "Retrying", class: "suggested" };
    return { label: "Connected", class: "healthy" };
  }

  function songDetailsMessage(status: NonNullable<IntelligenceState["songDetails"]>) {
    if (status.pending) return `Allstarr is checking ${status.pending} saved ${status.pending === 1 ? "listen" : "listens"} for more song details.`;
    const unmatched = status.unresolved + status.failed;
    if (unmatched) return `Allstarr could not add more song details to ${unmatched} saved ${unmatched === 1 ? "listen" : "listens"}. Your history is still saved.`;
    if (status.resolved) return `Allstarr added more song details to ${status.resolved} saved ${status.resolved === 1 ? "listen" : "listens"}.`;
    return "No saved listens are waiting for more song details.";
  }
</script>

<section class="intelligence-view">
  <header class="intelligence-header">
    <div class="route-heading-copy">
      <p class="eyebrow">Intelligence</p>
      <h2>{sectionIntro.title}</h2>
      <p>{sectionIntro.description}</p>
    </div>
    <div class="heading-tools" aria-busy={targetsLoading || loading}>
      {#if targetsLoading}
        <span class="scope-value" role="status"><small>Library</small><strong>Finding your music…</strong></span>
      {:else if selectedTarget && scopedTargets.length === 1}
        <span class="scope-value"><small>Library</small><strong>{libraryLabel(selectedTarget.libraryScopeId ?? "Music")}</strong><span>{serverLabel(selectedTarget.protocol)} · {selectedTarget.displayName}</span></span>
      {:else if scopedTargets.length > 1}
        <label class="field library-picker"><span>Library</span><SelectField value={selectedTargetId} label="Music library" options={targetOptions} onchange={(value) => void openTarget(value)} /></label>
      {:else}
        <span class="scope-value"><small>Library needed</small><strong>{mediaTargets.length ? "Finish indexing" : "Connect a music server"}</strong><Button variant="secondary" size="sm" href="#/integrations/services">Open services</Button></span>
      {/if}
      {#if data?.actions.canRun && activeSection === "discover"}
        {#if runStatus}<Badge state={runState === "succeeded" ? "healthy" : "suggested"}>{runStatus}</Badge>{/if}
        <Button disabled={Boolean(action)} onclick={() => void perform("run", () => intelligence.run(activeScope))}>{action === "run" ? "Starting…" : "Refresh recommendations"}</Button>
      {/if}
    </div>
  </header>

  <SegmentedNav items={[
    { id: "overview", label: "Overview", href: "#/intelligence?section=overview" },
    { id: "history", label: "History", href: "#/intelligence?section=history" },
    { id: "imports", label: "Import", href: "#/intelligence?section=imports" },
    { id: "discover", label: "Discover", href: "#/intelligence?section=discover" },
    { id: "automation", label: "Automation", href: "#/intelligence?section=automation" },
  ]} active={activeSection} label="Intelligence sections" class="intelligence-tabs" />

  {#if error}<div class="notice-error intelligence-error" role="alert"><span>{error}</span>{#if selectedTarget}<Button variant="outline" size="sm" disabled={loading} onclick={() => void load()}>{loading ? "Trying again…" : "Try again"}</Button>{/if}</div>{/if}

  {#if loading}
    <div class="intelligence-grid" role="status" aria-busy="true" aria-label="Loading Intelligence">
      {#each Array(3) as _}<Skeleton class="panel skeleton-panel" />{/each}
    </div>
  {:else if data?.state === "unauthorized" || data?.state === "error"}
    <RouteError
      eyebrow={data.state}
      title="This library is unavailable."
      message={data.message ?? "The library could not be loaded."}
      onRetry={load}
    />
  {:else if data}
    {#if data.state === "degraded"}
      <div class="degraded-banner" role="status"><span aria-hidden="true">!</span><p><strong>Some discovery sources need attention.</strong> Existing results remain available.</p></div>
    {/if}
    {#if activeSection === "discover" && data.actions.progress}
      <section class="panel run-progress" role="status">
        <div><p class="eyebrow">Refreshing recommendations</p><strong>{data.actions.progress.message}</strong>
          {#if data.actions.progress.provider || data.actions.progress.playlist || data.actions.progress.track}<small>{[data.actions.progress.provider, data.actions.progress.playlist, data.actions.progress.track].filter(Boolean).join(" · ")}</small>{/if}
          {#if data.actions.attemptCount && data.actions.maxAttempts}<small>Attempt {data.actions.attemptCount} of {data.actions.maxAttempts}{data.actions.failureCount ? ` · ${data.actions.failureCount} failed` : ""}</small>{/if}
        </div>
        {#if data.actions.progress.total}<Progress aria-label="Recommendation refresh progress" max={data.actions.progress.total} value={data.actions.progress.completed ?? 0} />{/if}
        {#if data.actions.canCancel && data.actions.latestJobId}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void perform("cancel", () => home.cancelJob(data!.actions.latestJobId!))}>{action === "cancel" ? "Cancelling…" : "Cancel refresh"}</Button>{/if}
      </section>
    {/if}
    {#if activeSection === "overview" || activeSection === "history" || activeSection === "imports"}
      <IntelligenceHistory scope={activeScope} section={historySection} policyEnabled={Boolean(data.policy?.enabled)} retentionDays={data.policy?.retentionDays ?? 30} onChanged={refresh} />
    {:else if activeSection === "automation"}
      <div class="settings-stack">
        <section class="panel automation-summary">
          <header><div><p class="eyebrow">Recommendation engine</p><h3>Automation</h3><p>Control saved listening, recommendation runs, generated playlists, and schedules in one place.</p></div>{#if runStatus}<Badge state={runState === "succeeded" ? "healthy" : "suggested"}>{runStatus}</Badge>{/if}</header>
          <dl>
            <div><dt>Last recommendation run</dt><dd>{runStatus ?? "Not run yet"}</dd></div>
            <div><dt>Next scheduled playlist</dt><dd>{nextSchedule?.nextRunAt ? new Date(nextSchedule.nextRunAt).toLocaleString() : "No run scheduled"}</dd></div>
            <div><dt>Generated playlists</dt><dd>{data.generatedSets.length}</dd></div>
          </dl>
          {#if data.actions.canRun}<Button disabled={Boolean(action)} onclick={() => void perform("run", () => intelligence.run(activeScope))}>{action === "run" ? "Starting…" : "Run recommendations now"}</Button>{/if}
        </section>
        <section class="panel privacy-card">
          <header><div><p class="eyebrow">Control</p><h3>Listening and recommendations</h3></div><Badge state={enabled ? "healthy" : "suggested"}>{enabled ? "Enabled" : "Off"}</Badge></header>
          <form onsubmit={(event) => {
            event.preventDefault();
            void perform("policy", () => intelligence.savePolicy(activeScope, {
              enabled, retentionDays, allowedSignalTypes: selectedSignals,
              enabledProviders: selectedProviders,
              targetCredentialReferenceId: targetCredentialReferenceId || null,
              expectedRevision: data!.policy?.revision ?? 0,
            }));
          }}>
            <div class="policy-basics">
              <label class="toggle-line" class:selected={enabled}><Checkbox bind:checked={enabled} /><span><strong>Save my listening automatically</strong><small>{enabled ? "Allstarr keeps private listening history and uses it for recommendations." : "Allstarr will not save new playback history or use it for recommendations."}</small></span></label>
              <label class="field"><span>Keep listening history for</span><SelectField value={String(retentionDays)} label="How long to keep listening history" onchange={(value) => { retentionDays = Number(value); }} options={[
                { value: "7", label: "7 days" }, { value: "30", label: "30 days" },
                { value: "90", label: "90 days" }, { value: "365", label: "1 year" }, { value: "3650", label: "10 years" },
              ]} /></label>
              {#if credentialOptions.length}
                <label class="field"><span>Where generated playlists are created</span><SelectField bind:value={targetCredentialReferenceId} label="Generated playlist destination" options={[{ value: "", label: "Use the connected media server" }, ...credentialOptions]} /></label>
              {/if}
            </div>
            <div class="policy-choices">
              <fieldset class="signal-choices"><legend>Recommendation actions</legend><p>Choose which listening actions teach Allstarr what you like.</p>{#each data.availableSignalTypes as item}<label class:selected={selectedSignals.includes(item.id)}><Checkbox checked={selectedSignals.includes(item.id)} onCheckedChange={(checked) => selectedSignals = toggle(selectedSignals, item.id, checked)} /> <span>{item.label}</span></label>{/each}</fieldset>
              <fieldset class="provider-choices"><legend>Recommendation sources</legend><p>Connected services Allstarr may use to find candidates. This does not import history or change source accounts.</p>{#each data.providers as provider}<label class:selected={selectedProviders.includes(provider.id)} class:unavailable={!provider.available}><Checkbox disabled={!provider.available} checked={selectedProviders.includes(provider.id)} onCheckedChange={(checked) => selectedProviders = toggle(selectedProviders, provider.id, checked)} /> <span><strong>{provider.label}</strong><small>{provider.description}</small></span><Badge state={provider.available ? "healthy" : "suggested"}>{providerStatus(provider.state)}</Badge></label>{/each}</fieldset>
            </div>
            <footer><Button variant="destructive" onclick={() => purgeOpen = true}>Turn off and clear</Button><Button type="submit" disabled={Boolean(action)}>{action === "policy" ? "Saving…" : "Save settings"}</Button></footer>
          </form>
        </section>
        <div class="settings-status-grid">
          <section class="panel status-card">
            <header><div><p class="eyebrow">Completed listens</p><h3>Listening services</h3></div></header>
            <ul class="status-list">{#each data.listeningServices ?? [] as service}
              {@const status = listeningServiceStatus(service)}
              <li class="status-row"><span><strong>{service.label}</strong><small>{listeningServiceMessage(service)}</small></span><Badge state={status.class}>{status.label}</Badge></li>
            {:else}<li class="muted">Allstarr will not send completed listens to another service.</li>{/each}</ul>
          </section>
          <section class="panel status-card">
            <header><div><p class="eyebrow">MusicBrainz</p><h3>Extra song details</h3></div></header>
            <p>{songDetailsMessage(data.songDetails ?? { pending: 0, resolved: 0, unresolved: 0, failed: 0 })}</p>
          </section>
        </div>
        <ListeningAppsCard scope={activeScope} policyEnabled={enabled} />
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
                  <div class="track-actions"><span class="score">{Math.round(item.score * 100)}%</span><Button variant="outline" size="xs" disabled={Boolean(action)} onclick={() => void perform(`similar:${item.id}`, () => intelligence.run(activeScope, [item.trackKey]))}>Similar</Button><Button variant="ghost" size="icon-xs" aria-label={`Dismiss ${item.title || item.trackKey}`} disabled={Boolean(action)} onclick={() => void perform(`dismiss:${item.id}`, () => intelligence.feedback(activeScope, item.id, "dismiss", item.feedback?.revision ?? 0))}><X size={16} aria-hidden="true" /></Button></div>
                </li>
              {/each}
            </ol>
          {:else if !data.policy?.enabled}<div class="compact-empty"><strong>Recommendations are off</strong><p><a class="touch-link" href="#/intelligence?section=automation">Turn on automatic history</a>, then import retained history or complete a play.</p></div>
          {:else if readyRecommendationSources}<div class="compact-empty"><strong>No recommendations yet</strong><p>Your sources are ready. Complete a play or import history inside the retention window, then refresh recommendations.</p></div>
          {:else}<div class="compact-empty"><strong>No recommendation sources are ready</strong><p><a class="touch-link" href="#/integrations/services">Connect or configure a source</a>, then refresh.</p></div>{/if}
        </section>

        <aside class="side-stack">
          <section class="panel profile-card"><p class="eyebrow">Recent listening</p><h3>Your profile</h3>{#if data.visualization.length}<ul class="profile-list">{#each data.visualization as item}<li><span>{item.label}</span><meter aria-label={item.label} min="0" max="1" value={item.value}>{item.value}</meter></li>{/each}</ul>{:else}<p class="muted">Turn on automatic history in <a class="touch-link" href="#/intelligence?section=automation">Automation</a>, then play music or import a history file.</p>{/if}</section>
          <section class="panel generated-card"><p class="eyebrow">Saved output</p><h3>Generated playlists</h3><ul class="generated-list">{#each data.generatedSets as item}<li class="generated-row"><span><strong>{item.name}</strong><small>{item.trackCount} tracks</small></span><Badge state={item.materialized ? "healthy" : "suggested"}>{generatedStatus(item)}</Badge></li>{:else}<li class="muted">No generated playlists yet.</li>{/each}</ul>{#if data.actions.canGenerate && data.actions.latestRunId}<form class="generate-form" onsubmit={(event) => { event.preventDefault(); void perform("generate", () => intelligence.generate(activeScope, data!.actions.latestRunId!, generatedName)); }}><label class="field"><span>Playlist name</span><input bind:value={generatedName} maxlength="200" required /></label><Button type="submit" disabled={Boolean(action)}>{action === "generate" ? "Creating…" : "Create playlist"}</Button></form>{/if}</section>
        </aside>
      </div>
    {/if}
  {/if}
</section>

<ConfirmDialog
  bind:open={purgeOpen}
  title="Clear private listening data for this library?"
  description="Allstarr will remove this account’s private listening history, recommendations, feedback, and saved imports from this music library. It will forget which playlists it generated, but it will not change Jellyfin playlists or connected Last.fm or ListenBrainz accounts."
  confirmLabel="Turn off and clear"
  cancelLabel="Keep my data"
  onConfirm={() => perform("purge", () => intelligence.purge(activeScope))}
/>

<style>
  .intelligence-view{display:grid;min-width:0;grid-template-columns:minmax(0,1fr);gap:1rem}.intelligence-header{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:1.5rem;padding:.25rem 0}.route-heading-copy h2{margin:.25rem 0;font-family:var(--font-display);font-size:2rem;letter-spacing:-.03em}.route-heading-copy p{margin:0}.route-heading-copy p:last-child{max-width:70ch;color:var(--color-ink-muted)}.heading-tools{display:flex;align-items:center;justify-content:flex-end;gap:.75rem}.scope-value{display:grid;min-width:11rem;gap:.08rem;border-radius:var(--radius-md);background:var(--color-panel);padding:.6rem .8rem}.scope-value small,.scope-value span{color:var(--color-ink-muted);font-size:var(--text-xs)}.scope-value :global([data-slot="button"]){margin-top:.35rem}.library-picker{width:18rem}.intelligence-error{display:flex;align-items:center;justify-content:space-between;gap:1rem}.run-progress{display:grid;grid-template-columns:minmax(0,1fr) minmax(10rem,.5fr) auto;align-items:center;gap:1rem;padding:1rem}.run-progress p{margin:0}.run-progress small{display:block;color:var(--color-ink-muted)}.settings-stack{display:grid;gap:1rem}.settings-status-grid{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.status-card{padding:1.15rem}.status-card h3,.status-card p{margin:.2rem 0}.status-list,.profile-list,.generated-list{margin:0;padding:0;list-style:none}.status-row{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.75rem 0}.status-row strong,.status-row small{display:block}.status-row small{color:var(--color-ink-muted)}.intelligence-grid{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(18rem,.8fr);gap:1rem}.recommendations,.profile-card,.generated-card,.privacy-card{padding:1.15rem}.recommendations>header,.privacy-card>header{display:flex;align-items:center;justify-content:space-between}.recommendations h3,.profile-card h3,.generated-card h3,.privacy-card h3{margin:.2rem 0 1rem}.recommendation-list{display:grid;margin:0;padding:0;list-style:none}.recommendation-list>li{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:.85rem;align-items:center;border-top:1px solid var(--color-edge);padding:.9rem 0}.track-copy small,summary{color:var(--color-ink-muted);font-size:.75rem}.track-copy details{margin-top:.35rem}.track-copy ul{margin:.4rem 0 0;padding-left:1.1rem;color:var(--color-ink-muted);font-size:.78rem}.side-stack{display:grid;align-content:start;gap:1rem}.profile-list li,.generated-row{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.7rem 0}.profile-card meter{width:55%;accent-color:var(--color-signal)}.generated-row span:first-child strong,.generated-row span:first-child small{display:block}.generated-row small{color:var(--color-ink-muted)}.generate-form{display:grid;gap:.75rem;margin-top:1rem}.automation-summary{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:1rem;padding:1.15rem}.automation-summary>header{grid-column:1/-1;display:flex;align-items:start;justify-content:space-between;gap:1rem}.automation-summary h3,.automation-summary p{margin:.2rem 0}.automation-summary header p:last-child{color:var(--color-ink-muted)}.automation-summary dl{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));margin:0;border:1px solid var(--color-edge);border-radius:var(--radius-md)}.automation-summary dl div{padding:.75rem}.automation-summary dl div+div{border-left:1px solid var(--color-edge)}.automation-summary dt{color:var(--color-ink-muted);font-size:var(--text-xs)}.automation-summary dd{margin:.2rem 0 0;font-weight:750}.privacy-card form{display:grid;gap:1rem}.policy-basics{display:grid;grid-template-columns:minmax(16rem,1.25fr) minmax(12rem,.75fr) minmax(16rem,1fr);align-items:start;gap:1rem}.policy-choices{display:grid;grid-template-columns:minmax(16rem,.8fr) minmax(22rem,1.2fr);gap:1.5rem;border-top:1px solid var(--color-edge);padding-top:1rem}.toggle-line{display:flex;align-items:flex-start;gap:.65rem;padding:.25rem 0;cursor:pointer}.toggle-line span>*{display:block}.toggle-line small,fieldset small{color:var(--color-ink-muted)}fieldset{display:grid;grid-template-columns:1fr 1fr;align-content:start;gap:0 .85rem;border:0;margin:0;padding:0}fieldset legend{grid-column:1/-1;font-weight:750}fieldset>p{grid-column:1/-1;max-width:65ch;margin:.2rem 0 .45rem;color:var(--color-ink-muted);font-size:var(--text-sm)}fieldset label{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:.65rem;min-height:2.75rem;border-top:1px solid var(--color-edge);padding:.55rem 0;cursor:pointer}.signal-choices label{grid-template-columns:auto minmax(0,1fr)}.signal-choices label span{font-weight:700}.provider-choices label span>*{display:block}.provider-choices label.unavailable{cursor:not-allowed}.privacy-card footer{display:flex;justify-content:space-between;gap:.75rem;border-top:1px solid var(--color-edge);padding-top:1rem}
  @media(max-width:1050px){.policy-basics{grid-template-columns:1fr 1fr}.policy-choices{grid-template-columns:1fr}}
  @media(max-width:900px){.intelligence-header{grid-template-columns:1fr}.heading-tools{justify-content:flex-start;flex-wrap:wrap}.library-picker{width:min(24rem,100%)}.run-progress{grid-template-columns:1fr}.settings-status-grid,.intelligence-grid{grid-template-columns:1fr}.automation-summary{grid-template-columns:1fr}.automation-summary>:global([data-slot="button"]){justify-self:start}}
  @media(max-width:620px){.route-heading-copy h2{font-size:1.6rem}.heading-tools,.scope-value,.library-picker{width:100%}:global(.intelligence-tabs :is(a,button)){min-width:4.5rem;flex:1 0 4.5rem;padding-inline:var(--space-1);font-size:var(--text-xs)}.policy-basics,.run-progress{grid-template-columns:1fr}.policy-choices{gap:1rem}.provider-choices,.signal-choices{grid-template-columns:1fr}.provider-choices label,.signal-choices label,fieldset legend,fieldset>p{grid-column:1}.recommendation-list>li{grid-template-columns:auto minmax(0,1fr)}.track-actions{grid-column:2;flex-wrap:wrap}.automation-summary dl{grid-template-columns:1fr}.automation-summary dl div+div{border-top:1px solid var(--color-edge);border-left:0}.automation-summary>:global([data-slot="button"]){width:100%}.privacy-card footer{flex-direction:column-reverse}.privacy-card footer>:global([data-slot="button"]){width:100%}.touch-link{display:inline-flex;min-height:var(--control-md);align-items:center}}
</style>
