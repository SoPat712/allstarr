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

  type IntelligenceSection = "overview" | "history" | "recommendations" | "imports" | "settings";

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
    initialSection === "history" || initialSection === "recommendations" || initialSection === "imports" || initialSection === "settings"
      ? initialSection
      : "overview",
  );
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
  <header class="route-heading">
    <div>
      <p class="eyebrow">Private discovery</p>
      <h2>Listen deeper, without exposing your history.</h2>
      <p>Recommendations, listening history, and generated playlists stay private to this account and music library.</p>
    </div>
    {#if data?.actions.canRun && activeSection === "recommendations"}
      <div class="heading-actions">
        {#if runStatus}<Badge state={runState === "succeeded" ? "healthy" : "suggested"}>{runStatus}</Badge>{/if}
        <Button disabled={Boolean(action)}
          onclick={() => void perform("run", () => intelligence.run(activeScope))}>
          {action === "run" ? "Starting…" : "Refresh recommendations"}
        </Button>
      </div>
    {/if}
  </header>

  <section class="panel scope-card" aria-busy={targetsLoading || loading}>
    <div class="scope-intro">
      <p class="eyebrow">Music library</p>
      <p>History, recommendations, and generated playlists stay scoped to one connected library.</p>
    </div>
    {#if targetsLoading}
      <span class="scope-card-status" role="status">Finding your library…</span>
    {:else if selectedTarget && scopedTargets.length === 1}
      <div class="library-choice">
        <strong>{libraryLabel(selectedTarget.libraryScopeId ?? "Music")}</strong>
        <span>{serverLabel(selectedTarget.protocol)} · {selectedTarget.displayName}</span>
      </div>
    {:else if scopedTargets.length > 1}
      <label class="field library-picker"><span>Use this library</span><SelectField value={selectedTargetId} label="Music library" options={targetOptions} onchange={(value) => void openTarget(value)} /></label>
    {:else}
      <div class="scope-card-status">
        <strong>{mediaTargets.length ? "Finish indexing your music library" : "Connect a music server"}</strong>
        <span>{mediaTargets.length ? "Allstarr found your server, but it has not indexed a music library yet." : "Connect Jellyfin or Subsonic before opening Intelligence."}</span>
        <Button variant="secondary" href="#/sources">Open Sources</Button>
      </div>
    {/if}
    {#if !targetsLoading && selectedTarget && error}
      <Button variant="secondary" disabled={loading} onclick={() => void load()}>{loading ? "Trying again…" : "Try again"}</Button>
    {/if}
  </section>

  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

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
    {#if activeSection === "recommendations" && data.actions.progress}
      <section class="panel run-progress" role="status">
        <div><p class="eyebrow">Refreshing recommendations</p><strong>{data.actions.progress.message}</strong>
          {#if data.actions.progress.provider || data.actions.progress.playlist || data.actions.progress.track}<small>{[data.actions.progress.provider, data.actions.progress.playlist, data.actions.progress.track].filter(Boolean).join(" · ")}</small>{/if}
          {#if data.actions.attemptCount && data.actions.maxAttempts}<small>Attempt {data.actions.attemptCount} of {data.actions.maxAttempts}{data.actions.failureCount ? ` · ${data.actions.failureCount} failed` : ""}</small>{/if}
        </div>
        {#if data.actions.progress.total}<Progress aria-label="Recommendation refresh progress" max={data.actions.progress.total} value={data.actions.progress.completed ?? 0} />{/if}
        {#if data.actions.canCancel && data.actions.latestJobId}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void perform("cancel", () => home.cancelJob(data!.actions.latestJobId!))}>{action === "cancel" ? "Cancelling…" : "Cancel refresh"}</Button>{/if}
      </section>
    {/if}
    <SegmentedNav items={[
      { id: "overview", label: "Overview", href: "#/intelligence?section=overview" },
      { id: "history", label: "History", href: "#/intelligence?section=history" },
      { id: "recommendations", label: "Recommendations", href: "#/intelligence?section=recommendations" },
      { id: "imports", label: "Imports", href: "#/intelligence?section=imports" },
      { id: "settings", label: "Settings", href: "#/intelligence?section=settings" },
    ]} active={activeSection} label="Intelligence sections" class="intelligence-tabs" />

    {#if activeSection === "overview" || activeSection === "history" || activeSection === "imports"}
      <IntelligenceHistory scope={activeScope} section={activeSection} policyEnabled={Boolean(data.policy?.enabled)} retentionDays={data.policy?.retentionDays ?? 30} />
    {:else if activeSection === "settings"}
      <div class="settings-stack">
        <section class="panel privacy-card">
          <header><div><p class="eyebrow">Control</p><h3>Privacy and sources</h3></div><Badge state={enabled ? "healthy" : "suggested"}>{enabled ? "Enabled" : "Off"}</Badge></header>
          <form onsubmit={(event) => {
            event.preventDefault();
            void perform("policy", () => intelligence.savePolicy(activeScope, {
              enabled, retentionDays, allowedSignalTypes: selectedSignals,
              enabledProviders: selectedProviders,
              targetCredentialReferenceId: targetCredentialReferenceId || null,
              expectedRevision: data!.policy?.revision ?? 0,
            }));
          }}>
            <label class="toggle-line" class:selected={enabled}><Checkbox bind:checked={enabled} /><span><strong>Save my listening automatically</strong><small>{enabled ? "Allstarr keeps private listening history and uses it for recommendations." : "Allstarr will not save new playback history or use it for recommendations."}</small></span></label>
            <label class="field"><span>Keep listening history for</span><SelectField value={String(retentionDays)} label="How long to keep listening history" onchange={(value) => { retentionDays = Number(value); }} options={[
              { value: "7", label: "7 days" }, { value: "30", label: "30 days" },
              { value: "90", label: "90 days" }, { value: "365", label: "1 year" }, { value: "3650", label: "10 years" },
            ]} /></label>
            {#if credentialOptions.length}
              <label class="field"><span>Where generated playlists are created</span><SelectField bind:value={targetCredentialReferenceId} label="Generated playlist destination" options={[{ value: "", label: "Use the connected media server" }, ...credentialOptions]} /></label>
            {/if}
            <fieldset class="signal-choices"><legend>Use these actions for recommendations</legend>{#each data.availableSignalTypes as item}<label class:selected={selectedSignals.includes(item.id)}><Checkbox checked={selectedSignals.includes(item.id)} onCheckedChange={(checked) => selectedSignals = toggle(selectedSignals, item.id, checked)} /> <span>{item.label}</span></label>{/each}</fieldset>
            <fieldset class="provider-choices"><legend>Sources</legend>{#each data.providers as provider}<label class:selected={selectedProviders.includes(provider.id)} class:unavailable={!provider.available}><Checkbox disabled={!provider.available} checked={selectedProviders.includes(provider.id)} onCheckedChange={(checked) => selectedProviders = toggle(selectedProviders, provider.id, checked)} /> <span><strong>{provider.label}</strong><small>{provider.description}</small><Badge state={provider.available ? "healthy" : "suggested"}>{providerStatus(provider.state)}</Badge></span></label>{/each}</fieldset>
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
          {:else if !data.policy?.enabled}<div class="compact-empty"><strong>Recommendations are off</strong><p><a class="touch-link" href="#/intelligence?section=settings">Turn on automatic history</a>, then import retained history or complete a play.</p></div>
          {:else if readyRecommendationSources}<div class="compact-empty"><strong>No recommendations yet</strong><p>Your sources are ready. Complete a play or import history inside the retention window, then refresh recommendations.</p></div>
          {:else}<div class="compact-empty"><strong>No recommendation sources are ready</strong><p><a class="touch-link" href="#/sources">Connect or configure a source</a>, then refresh.</p></div>{/if}
        </section>

        <aside class="side-stack">
          <section class="panel profile-card"><p class="eyebrow">Recent listening</p><h3>Your profile</h3>{#if data.visualization.length}<ul class="profile-list">{#each data.visualization as item}<li><span>{item.label}</span><meter aria-label={item.label} min="0" max="1" value={item.value}>{item.value}</meter></li>{/each}</ul>{:else}<p class="muted">Turn on automatic history in <a class="touch-link" href="#/intelligence?section=settings">Settings</a>, then play music or import a history file.</p>{/if}</section>
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
  .intelligence-view{display:grid;min-width:0;grid-template-columns:minmax(0,1fr);gap:1.25rem}.route-heading{display:flex;align-items:end;justify-content:space-between;gap:1rem}.route-heading h2{margin:.25rem 0;font-family:var(--font-display);font-size:clamp(1.5rem,3vw,2.2rem)}.route-heading p:last-child{color:var(--color-ink-muted)}.heading-actions{display:flex;align-items:center;gap:.75rem}.scope-card{display:flex;align-items:center;justify-content:space-between;gap:1.25rem;padding:1rem}.scope-intro{min-width:0}.scope-intro p{margin:0}.scope-intro p:last-child{margin-top:.2rem;color:var(--color-ink-muted)}.library-choice{display:grid;min-width:min(22rem,45%);border-left:1px solid var(--color-edge);padding-left:1rem}.library-choice span,.scope-card-status span{color:var(--color-ink-muted);font-size:.8rem}.library-picker{width:min(24rem,45%)}.scope-card-status{display:flex;align-items:center;gap:.75rem}.scope-card-status strong,.scope-card-status span{display:block}.run-progress{display:grid;grid-template-columns:minmax(0,1fr) minmax(10rem,.5fr) auto;align-items:center;gap:1rem;padding:1rem}.run-progress p{margin:0}.run-progress small{display:block;color:var(--color-ink-muted)}.settings-stack{display:grid;gap:1rem}.settings-status-grid{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.status-card{padding:1.15rem}.status-card h3,.status-card p{margin:.2rem 0}.status-list,.profile-list,.generated-list{margin:0;padding:0;list-style:none}.status-row{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.75rem 0}.status-row strong,.status-row small{display:block}.status-row small{color:var(--color-ink-muted)}.intelligence-grid{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(18rem,.8fr);gap:1rem}.recommendations,.profile-card,.generated-card,.privacy-card{padding:1.15rem}.recommendations>header,.privacy-card>header{display:flex;align-items:center;justify-content:space-between}.recommendations h3,.profile-card h3,.generated-card h3,.privacy-card h3{margin:.2rem 0 1rem}.recommendation-list{display:grid;margin:0;padding:0;list-style:none}.recommendation-list>li{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:.85rem;align-items:center;border-top:1px solid var(--color-edge);padding:.9rem 0}.track-copy small,summary{color:var(--color-ink-muted);font-size:.75rem}.track-copy details{margin-top:.35rem}.track-copy ul{margin:.4rem 0 0;padding-left:1.1rem;color:var(--color-ink-muted);font-size:.78rem}.side-stack{display:grid;align-content:start;gap:1rem}.profile-list li,.generated-row{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.7rem 0}.profile-card meter{width:55%;accent-color:var(--color-signal)}.generated-row span:first-child strong,.generated-row span:first-child small{display:block}.generated-row small{color:var(--color-ink-muted)}.generate-form{display:grid;gap:.75rem;margin-top:1rem}.privacy-card form{display:grid;grid-template-columns:minmax(14rem,.6fr) 1fr 1fr;gap:1rem}.toggle-line,fieldset label{display:flex;align-items:flex-start;gap:.65rem;border:1px solid var(--color-edge);border-radius:var(--radius-md);background:var(--color-panel-raised);padding:.75rem;cursor:pointer}.toggle-line.selected,fieldset label.selected{border-color:color-mix(in srgb,var(--color-signal) 70%,var(--color-edge));background:color-mix(in srgb,var(--color-signal) 8%,var(--color-panel-raised))}.toggle-line span>*,fieldset label span>*{display:block}.toggle-line small,fieldset small{color:var(--color-ink-muted)}fieldset{display:grid;align-content:start;gap:.5rem;border:0;margin:0;padding:0}fieldset legend{margin-bottom:.55rem;font-weight:750}.signal-choices label{align-items:center;padding:.55rem .65rem}.signal-choices label span{font-weight:700}.provider-choices label.unavailable{cursor:not-allowed}.privacy-card footer{grid-column:1/-1;display:flex;justify-content:space-between;gap:.75rem;border-top:1px solid var(--color-edge);padding-top:1rem}
  @media(max-width:900px){.scope-card{align-items:stretch;flex-direction:column}.library-choice,.library-picker{width:100%;min-width:0;border-left:0;border-top:1px solid var(--color-edge);padding-top:1rem;padding-left:0}.run-progress{grid-template-columns:1fr}.settings-status-grid,.intelligence-grid{grid-template-columns:1fr}.privacy-card form{grid-template-columns:1fr 1fr}}
  @media(max-width:620px){.route-heading{align-items:stretch;flex-direction:column}.scope-card-status{align-items:stretch;flex-direction:column}.privacy-card form,.run-progress{grid-template-columns:1fr}.recommendation-list>li{grid-template-columns:auto minmax(0,1fr)}.track-actions{grid-column:2;flex-wrap:wrap}.privacy-card footer{grid-column:auto;flex-direction:column-reverse}.privacy-card footer>:global([data-slot="button"]){width:100%}.touch-link{display:inline-flex;min-height:var(--control-md);align-items:center}}
</style>
