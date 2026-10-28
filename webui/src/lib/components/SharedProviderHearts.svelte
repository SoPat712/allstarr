<script lang="ts">
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { ApiError, providerFavoriteLinks, type FavoriteLinkPair, type FavoriteLinkPreview,
    type ProviderFavoriteLink, type ProviderFavoriteMember } from "$lib/api";
  import { newReportRequestId as newRequestId, supportDate } from "$lib/support";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Skeleton } from "$lib/components/ui/skeleton";

  let { administrator }: { administrator: boolean } = $props();
  let members = $state<ProviderFavoriteMember[]>([]);
  let links = $state<ProviderFavoriteLink[]>([]);
  let activeProtocol = $state("");
  let loaded = $state(false);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let message = $state("");
  let jellyfinId = $state("");
  let subsonicId = $state("");
  let preview = $state<FavoriteLinkPreview | null>(null);
  let reviewedKey = $state("");
  let confirmed = $state(false);
  let requestId = "";
  const retryRequests = new Map<string, { requestId: string; expectedRevision: number }>();
  let nextJellyfin = $state<string | null>(null);
  let nextSubsonic = $state<string | null>(null);
  let nextLinks = $state<string | null>(null);
  let disposed = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let polls = $state(0);
  let pollOffset = 0;
  const reads = new AbortController();
  const jellyfin = $derived(members.find(member => member.userId === jellyfinId));
  const subsonic = $derived(members.find(member => member.userId === subsonicId));
  const eligible = (member: ProviderFavoriteMember) => member.participation === "available" && !member.linkId;
  const jellyfinMembers = $derived(members.filter(member => member.protocol === "jellyfin" && eligible(member)));
  const subsonicMembers = $derived(members.filter(member => member.protocol === "subsonic" && eligible(member)));
  const pairKey = $derived(JSON.stringify([jellyfinId, jellyfin?.identityFingerprint, subsonicId, subsonic?.identityFingerprint]));
  const pairReady = $derived(Boolean(jellyfin && subsonic && eligible(jellyfin) && eligible(subsonic)));
  const active = $derived(links.filter(link => ["pending", "running", "retry_scheduled"].includes(link.union.state)));

  $effect(() => {
    if (reviewedKey && (reviewedKey !== pairKey || !pairReady)) {
      preview = null; reviewedKey = ""; confirmed = false; requestId = "";
    }
  });
  const protocol = (value: string) => value.toLowerCase() === "jellyfin" ? "Jellyfin"
    : value.toLowerCase() === "subsonic" ? "Subsonic" : "Not available";
  const profile = (member: ProviderFavoriteMember) =>
    `${member.displayName || "Unnamed profile"} · ${protocol(member.protocol)} · ${member.instanceLabel} · ${member.profileCode}`;
  const options = (items: ProviderFavoriteMember[]) => items.map(member => ({ value: member.userId, label: profile(member) }));
  const stateLabel = (link: ProviderFavoriteLink) => link.state === "shared" ? "Shared"
    : link.state === "needs_attention" ? "Needs attention" : "Combining hearts";
  const participation = (member: ProviderFavoriteMember) => member.participation === "disabled" ? "Disabled profile"
    : member.participation === "synthetic" ? "Test listener"
    : member.participation === "identity_changed" ? "Profile identity changed" : "";
  const count = (value: number | null | undefined) => typeof value === "number" && Number.isFinite(value) && value >= 0
    ? value.toLocaleString() : "Not available";
  function failure(cause: unknown, fallback: string) {
    if (cause instanceof ApiError) {
      const details = cause.details as { message?: string; error?: string } | null;
      return details?.message || (details?.error && /\s/.test(details.error) ? details.error : fallback);
    }
    return fallback;
  }
  function pair(): FavoriteLinkPair {
    if (!jellyfin || !subsonic || !pairReady) throw new Error("Choose two available profiles.");
    return { jellyfinUserId: jellyfin.userId, jellyfinFingerprint: jellyfin.identityFingerprint,
      subsonicUserId: subsonic.userId, subsonicFingerprint: subsonic.identityFingerprint };
  }
  function changed() {
    preview = null; reviewedKey = ""; confirmed = false; requestId = ""; error = ""; message = "";
  }
  function acceptLink(detail: ProviderFavoriteLink) {
    links = [detail, ...links.filter(link => link.id !== detail.id)];
    const linked = new Map([detail.jellyfin, detail.subsonic].map(member => [member.userId, member]));
    members = members.map(member => linked.has(member.userId) ? { ...linked.get(member.userId)!, linkId: detail.id } : member);
    jellyfinId = ""; subsonicId = ""; changed(); polls = 0; schedule();
  }
  function schedule() {
    clearTimeout(timer);
    if (!disposed && active.length && polls < 120) timer = setTimeout(() => void poll(), 5000);
  }
  async function refresh() {
    if (!administrator || disposed || refreshing) return;
    refreshing = true;
    try {
      const [jf, sub, listed] = await Promise.all([
        providerFavoriteLinks.members("jellyfin", undefined, reads.signal),
        providerFavoriteLinks.members("subsonic", undefined, reads.signal),
        providerFavoriteLinks.list(undefined, reads.signal),
      ]);
      if (disposed) return;
      members = [...jf.items, ...sub.items]; links = listed.items; activeProtocol = listed.activeProtocol;
      nextJellyfin = jf.nextCursor; nextSubsonic = sub.nextCursor; nextLinks = listed.nextCursor;
      loaded = true; error = ""; polls = 0;
    } catch (cause) { if (!disposed) error = failure(cause, "Profiles and shared hearts could not be loaded. Refresh to try again."); }
    finally { if (!disposed) { refreshing = false; schedule(); } }
  }
  async function more(kind: "jellyfin" | "subsonic" | "links") {
    if (action || disposed) return;
    action = "more";
    try {
      if (kind === "links") {
        const data = await providerFavoriteLinks.list(nextLinks ?? undefined, reads.signal);
        if (disposed) return;
        links = [...new Map([...links, ...data.items].map(link => [link.id, link])).values()]; nextLinks = data.nextCursor;
      } else {
        const data = await providerFavoriteLinks.members(kind, (kind === "jellyfin" ? nextJellyfin : nextSubsonic) ?? undefined, reads.signal);
        if (disposed) return;
        members = [...new Map([...members, ...data.items].map(member => [member.userId, member])).values()];
        if (kind === "jellyfin") nextJellyfin = data.nextCursor; else nextSubsonic = data.nextCursor;
      }
      error = "";
    } catch (cause) { if (!disposed) error = failure(cause, "More profiles could not be loaded. Try again."); }
    finally { if (!disposed) { action = ""; schedule(); } }
  }
  async function poll() {
    if (disposed || !active.length) return;
    if (action || refreshing) { schedule(); return; }
    polls++;
    const current = [...active.slice(pollOffset), ...active.slice(0, pollOffset)].slice(0, 4);
    pollOffset = (pollOffset + current.length) % Math.max(1, active.length);
    try {
      const incoming = await Promise.all(current.map(link => providerFavoriteLinks.detail(link.id, reads.signal)));
      if (disposed) return;
      const byId = new Map(incoming.map(link => [link.id, link]));
      links = links.map(link => byId.get(link.id) ?? link);
    } catch { if (!disposed) error = "Sharing progress could not be refreshed. Your selections have been kept."; }
    finally { if (!disposed) schedule(); }
  }
  async function review() {
    if (!pairReady || action || disposed) return;
    const input = pair(), key = pairKey;
    action = "preview"; error = ""; preview = null; confirmed = false;
    try {
      const result = await providerFavoriteLinks.preview(input);
      if (disposed || key !== pairKey) return;
      if (result.jellyfin.userId !== input.jellyfinUserId || result.subsonic.userId !== input.subsonicUserId ||
          result.jellyfin.identityFingerprint !== input.jellyfinFingerprint || result.subsonic.identityFingerprint !== input.subsonicFingerprint)
        throw new Error("Changed profiles");
      if (result.existingLinkId) {
        const detail = await providerFavoriteLinks.detail(result.existingLinkId, reads.signal);
        if (disposed || key !== pairKey) return;
        acceptLink(detail); message = "These profiles already share provider hearts.";
      } else { preview = result; reviewedKey = key; }
    } catch (cause) { if (!disposed) error = failure(cause, "Profiles changed or could not be reviewed. Refresh profiles before continuing."); }
    finally { if (!disposed) action = ""; }
  }
  async function create() {
    if (!preview || !confirmed || !pairReady || reviewedKey !== pairKey || action || disposed) return;
    action = "create"; error = "";
    requestId ||= newRequestId();
    try {
      const result = await providerFavoriteLinks.create({ ...pair(), requestId, confirmedSamePerson: true,
        confirmationVersion: preview.confirmationVersion });
      if (disposed) return;
      const detail = await providerFavoriteLinks.detail(result.linkId, reads.signal);
      if (disposed) return;
      acceptLink(detail);
      message = detail.union.state === "succeeded" ? "Provider hearts linked. Saved hearts have been combined."
        : "Provider hearts linked. Saved hearts are being combined.";
    } catch (cause) { if (!disposed) error = failure(cause, "The profiles could not be linked. Try again with the same selection."); }
    finally { if (!disposed) action = ""; }
  }
  async function retry(link: ProviderFavoriteLink) {
    if (!link.union.canRetry || action || disposed) return;
    action = link.id; error = "";
    let input = retryRequests.get(link.id);
    if (!input || input.expectedRevision !== link.revision) {
      input = { requestId: newRequestId(), expectedRevision: link.revision }; retryRequests.set(link.id, input);
    }
    try {
      const receipt = await providerFavoriteLinks.retry(link.id, input);
      if (disposed) return;
      const detail = await providerFavoriteLinks.detail(receipt.linkId, reads.signal);
      if (disposed) return;
      links = links.map(item => item.id === detail.id ? detail : item);
      retryRequests.delete(link.id); polls = 0; schedule();
    } catch (cause) { if (!disposed) error = failure(cause, "Combining hearts could not be retried. Refresh its status before trying again."); }
    finally { if (!disposed) action = ""; }
  }
  const scheduler = createRefreshScheduler(refresh);
  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); clearTimeout(timer); reads.abort(); };
  });
</script>

{#if administrator}
<section class="settings-card p-5 my-4 space-y-3" aria-labelledby="shared-hearts-title">
  <div class="panel-heading">
    <div><h3 id="shared-hearts-title" class="font-semibold">Shared provider hearts</h3><p>Combine provider hearts for two profiles of the same person.</p></div>
    <div class="panel-heading-actions"><RefreshButton variant="secondary" disabled={Boolean(action)} onrefresh={refresh} label="Refresh profiles" /></div>
  </div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if message}<p class="notice-info" role="status">{message}</p>{/if}
  {#if !loaded && !error}<Skeleton class="skeleton-panel" aria-label="Loading shared hearts" aria-busy="true" />{:else if loaded}
    <p class="setting-help">Current music protocol: {protocol(activeProtocol)}. Each profile keeps its library access, source accounts, history and settings.</p>
    {#each links as link (link.id)}
      <article class="settings-card" aria-label="Linked profiles">
        <div class="settings-row"><div><strong>{profile(link.jellyfin)}</strong><small>{profile(link.subsonic)}</small></div>
          <span role="status" aria-label="Sharing status"><Badge state={link.state === "shared" ? "healthy" : link.state === "needs_attention" ? "degraded" : "pending"}>{stateLabel(link)}</Badge></span></div>
        {#each [link.jellyfin, link.subsonic] as member}
          {#if participation(member)}<p class="notice-info">{member.displayName || "Unnamed profile"}: {participation(member)}. Available linked profiles keep their shared hearts.</p>{/if}
        {/each}
        <div class="settings-row"><div><strong>Saved hearts</strong><small>{count(link.union.processedRows)} rows processed · {count(link.union.seededItems)} hearts added</small></div>
          <span>{link.union.completedAt ? supportDate(link.union.completedAt) : link.union.state === "failed" || link.union.state === "cancelled"
            ? "Stopped" : link.union.state === "succeeded" ? "Completed; time not available" : "Combining existing hearts"}</span></div>
        {#if link.union.state === "failed" || link.union.state === "cancelled"}<p class="notice-info">Combining saved hearts stopped. A retry continues from the saved progress.</p>{/if}
        {#if link.union.canRetry}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void retry(link)}>Retry combining hearts</Button>{/if}
      </article>
    {/each}
    {#if nextLinks}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void more("links")}>More linked profiles</Button>{/if}
    {#if polls >= 120 && active.length}<p class="notice-info" role="status">Combining hearts is still running. Refresh its status to check again.</p>{/if}
    <form class="settings-content" aria-label="Link listener profiles" onsubmit={event => { event.preventDefault(); void review(); }}>
      <div class="settings-fields">
        <label class="field"><span>Jellyfin profile</span><SelectField value={jellyfinId} label="Jellyfin profile" placeholder="Choose a Jellyfin profile" options={options(jellyfinMembers)} disabled={Boolean(action) || !jellyfinMembers.length} onchange={value => { jellyfinId = value; changed(); }} /></label>
        <label class="field"><span>Subsonic profile</span><SelectField value={subsonicId} label="Subsonic profile" placeholder="Choose a Subsonic profile" options={options(subsonicMembers)} disabled={Boolean(action) || !subsonicMembers.length} onchange={value => { subsonicId = value; changed(); }} /></label>
      </div>
      {#if !jellyfinMembers.length}<p class="setting-help">No eligible Jellyfin profile is available. Profiles must have previously signed in, be enabled, and not already be linked or used for testing.</p>{/if}
      {#if !subsonicMembers.length}<p class="setting-help">No eligible Subsonic profile is available. Profiles must have previously signed in, be enabled, and not already be linked or used for testing.</p>{/if}
      {#if nextJellyfin}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void more("jellyfin")}>More Jellyfin profiles</Button>{/if}
      {#if nextSubsonic}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void more("subsonic")}>More Subsonic profiles</Button>{/if}
      <div><Button type="submit" variant="secondary" disabled={!pairReady || Boolean(action)}>{action === "preview" ? "Reviewing…" : "Review profiles"}</Button></div>
    </form>
    {#if preview}
      <section class="settings-card space-y-3" aria-label="Confirm profiles">
        <h4 class="font-semibold">Confirm these profiles</h4>
        <p>{profile(preview.jellyfin)}</p><p>{profile(preview.subsonic)}</p>
        <p>Combine their existing provider hearts; future un-hearts apply to both. The initial union cannot be undone here. Disabling a profile later keeps hearts already shared with the other profile.</p>
        <label class="settings-row"><span>These profiles belong to the same person</span><span class="settings-toggle"><Checkbox aria-label="These profiles belong to the same person" checked={confirmed} disabled={Boolean(action)} onCheckedChange={value => confirmed = value === true} /></span></label>
        <Button disabled={!confirmed || !pairReady || Boolean(action)} onclick={() => void create()}>{action === "create" ? "Linking…" : "Share provider hearts"}</Button>
      </section>
    {/if}
  {/if}
</section>
{/if}
