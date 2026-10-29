<script lang="ts">
  import { onMount } from "svelte";
  import { taste, type TasteNudge, type TasteProfile } from "$lib/api";
  import { nudgeMarker, nudgeName, tasteSummary } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Popover } from "$lib/components/ui/popover";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  let profile = $state<TasteProfile | null>(null), loading = $state(true), error = $state("");
  let action = $state(false), resetOpen = $state(false), clearNudges = $state(false), resetError = $state("");
  let selected = $state(""), duration = $state("today");
  let pending: Promise<void> | null = null, disposed = false;
  const layers = $derived(profile ? [{ title: "Lately", data: profile.lately }, { title: "Usually", data: profile.usually }] : []);
  const tags = $derived([...new Set(layers.flatMap(layer => layer.data.tags.map(row => row.name)))].slice(0, 12));
  const artists = $derived([...new Set(layers.flatMap(layer => layer.data.artists.map(row => row.name)))].slice(0, 8));
  const summary = $derived(tasteSummary(profile?.lately.tags.map(item => item.name) ?? [], profile?.usually.tags.map(item => item.name) ?? []));
  const summaryTags = $derived([...new Set(layers.flatMap(layer => layer.data.tags.map(row => row.name)))]);
  const summaryParts = $derived.by(() => {
    const pattern = summaryTags.map(name => name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|");
    return pattern ? summary.split(new RegExp(`(${pattern})`, "g")) : [summary];
  });
  async function load() {
    if (pending) return pending;
    pending = (async () => {
      try { const next = await taste.get(); if (!disposed) { profile = next; error = ""; } }
      catch { if (!disposed) error = "Your taste could not be loaded. Refresh to try again."; }
      finally { if (!disposed) loading = false; pending = null; }
    })(); return pending;
  }
  let toggleRevision = $state(0);
  async function update(operation: () => Promise<TasteProfile>, resetting = false) {
    if (action) return; action = true;
    try { await pending; const next = await operation(); if (!disposed) { profile = next; error = ""; selected = ""; if (resetting) resetOpen = false; } }
    catch { if (!disposed) { toggleRevision++; if (resetting) resetError = "Your taste could not be reset. Try again."; else toast.error("Your taste could not be updated. Try again."); } }
    finally { if (!disposed) action = false; }
  }
  function nudge(kind: "tag" | "artist", name: string, mode: TasteNudge["mode"]) { return update(() => taste.nudge(kind, name, mode, mode !== "never" && duration === "today", Intl.DateTimeFormat().resolvedOptions().timeZone)); }
  const scheduler = createRefreshScheduler(async () => { if (!action) await load(); });
  onMount(() => { void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule); return () => { disposed = true; unsubscribe(); scheduler.cancel(); }; });
</script>
{#snippet chips(kind: "tag" | "artist", names: string[])}
  <div class="taste-chips">{#each names as name (`${kind}:${name}`)}
    {@const nudgeValue = profile?.nudges.find(item => item.kind === kind && item.name.toLowerCase() === name.toLowerCase())}
    <Popover.Root open={selected === `${kind}:${name}`} onOpenChange={open => { selected = open ? `${kind}:${name}` : ""; if (open) duration = "today"; }}>
      <Popover.Trigger class={`${buttonVariants({ variant: "secondary" })} taste-chip`} aria-label={`Steer ${name}`} disabled={action}>{name}{#if nudgeValue}<span aria-hidden="true"> {nudgeMarker(nudgeValue.mode)}</span>{/if}</Popover.Trigger>
      <Popover.Portal><Popover.Content class="bits-menu taste-popover" sideOffset={8}>
        <SegmentedNav label={`Duration for ${name}`} items={[{ id: "today", label: "Today only" }, { id: "persistent", label: "Until I change it" }]} active={duration} onchange={value => duration = value} />
        <div class="nudge-options">{#each ["more", "less", "never"] as mode}<Button variant="ghost" disabled={action} onclick={() => nudge(kind, name, mode as TasteNudge["mode"])}>{mode === "more" ? "More" : mode === "less" ? "Less" : "Never"}</Button>{/each}</div>
      </Popover.Content></Popover.Portal>
    </Popover.Root>
  {/each}</div>
{/snippet}
<div class="your-taste">
  <div class="section-heading"><h2>Your taste</h2><RefreshButton variant="secondary" label="Refresh taste" disabled={action} onrefresh={load} /></div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading your taste" />{:else if profile}
    <section class="settings-card summary"><p>{#each summaryParts as part}{#if summaryTags.includes(part)}<strong>{part}</strong>{:else}{part}{/if}{/each}</p><small>{profile.latelyWeight >= .5 ? "Today's listening counts most right now." : "Your usual taste counts most right now."}</small></section>
    <section class="panel taste-section"><h2>Steer your mixes</h2><p>Tap a style to hear more or less of it.</p>{@render chips("tag", tags)}<h3>Artists</h3>{@render chips("artist", artists)}</section>
    <section class="panel taste-section" aria-label="Hidden"><h2>Hidden</h2>
      {#if profile.nudges.some(item => item.mode === "never")}<ul>{#each profile.nudges.filter(item => item.mode === "never") as item (`${item.kind}:${item.name}`)}<li><span>{nudgeName(item)}</span><Button variant="ghost" disabled={action} aria-label={`Undo ${nudgeName(item)}`} onclick={() => update(() => taste.remove(item.kind, item.name))}>Undo</Button></li>{/each}</ul>{:else}<p>Nothing hidden.</p>{/if}
    </section>
    <section class="panel taste-section" aria-label="Active nudges"><h2>Active nudges</h2>
      <ul>{#each profile.nudges.filter(item => item.mode !== "never") as item (`${item.kind}:${item.name}`)}<li><span>{item.mode === "more" ? "More" : "Less"} {nudgeName(item)} · {item.expiresAt ? "today" : "until I change it"}</span><button class="icon-button" type="button" disabled={action} aria-label={`Remove ${nudgeName(item)}`} onclick={() => update(() => taste.remove(item.kind, item.name))}>×</button></li>{/each}</ul>
      {#if !profile.nudges.some(item => item.mode !== "never")}<p>No active nudges.</p>{/if}
    </section>
    <section class="settings-card taste-settings"><header><h2>Privacy & learning</h2></header>
      {#key toggleRevision}
      <label class="settings-row"><div><strong>Private listening (pause learning)</strong></div><span class="settings-toggle"><Checkbox checked={profile.paused} disabled={action} aria-label="Private listening (pause learning)" onCheckedChange={paused => update(() => taste.pause(paused === true))} /></span></label>
      <label class="settings-row"><div><strong>Use imported history</strong><small>Imported plays shape your usual taste.</small></div><span class="settings-toggle"><Checkbox checked={profile.useImportedHistory ?? true} disabled={action} aria-label="Use imported history" onCheckedChange={enabled => update(() => taste.importedHistory(enabled === true))} /></span></label>
      {/key}
      <footer><Button variant="secondary" disabled={action} onclick={() => { clearNudges = false; resetError = ""; resetOpen = true; }}>Reset taste</Button></footer>
    </section>
    <details class="settings-card taste-section"><summary class="disclosure-summary"><DisclosureLabel title="See the details" /></summary><div class="taste-layers">{#each layers as layer}<section><h3>{layer.title}</h3>{#each [{ title: "Styles", rows: layer.data.tags }, { title: "Artists", rows: layer.data.artists }] as group}<h4>{group.title}</h4>{#each group.rows.slice(0, 5) as row}<div class="taste-strength"><strong>{row.name}</strong><Progress value={Math.max(0, Math.min(100, row.strength))} aria-label={`${row.name} strength`} /></div>{/each}{#if !group.rows.length}<p>No {group.title.toLowerCase()} yet.</p>{/if}{/each}</section>{/each}</div></details>
  {/if}
</div>
<ConfirmDialog bind:open={resetOpen} title="Reset taste" description="Start learning your taste again from new plays and skips?" confirmLabel="Reset taste" disabled={action} closeOnConfirm={false} error={resetError} onConfirm={() => update(() => taste.reset(clearNudges), true)}><label class="reset-nudges"><span class="settings-toggle"><Checkbox bind:checked={clearNudges} disabled={action} aria-label="Also remove my nudges" /></span><span>Also remove my nudges</span></label></ConfirmDialog>
<style>
  .your-taste { display: grid; min-width: 0; gap: var(--section-gap); }
  .section-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; } h3 { font-weight: 600; margin-block: var(--space-4) var(--space-2); }
  .taste-section, .summary { padding: var(--surface-gutter); min-width: 0; }
  .summary p { font-size: 1.125rem; line-height: 1.6; } small, .taste-section p { color: var(--color-ink-muted); }
  .summary small { display: block; margin-top: var(--space-3); }
  .taste-chips { display: flex; flex-wrap: wrap; gap: var(--space-2); margin-top: var(--space-3); }
  .taste-chips :global(.taste-chip) { min-height: 44px; border-radius: var(--radius-lg); }
  :global(.taste-popover) { max-width: calc(100vw - 32px); padding: var(--space-3); }
  :global(.nudge-options) { display: flex; gap: var(--space-2); justify-content: space-between; margin-top: var(--space-3); }
  ul { list-style: none; padding: 0; margin: var(--space-3) 0 0; } li { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); min-height: 44px; } li + li { border-top: 1px solid var(--color-edge); } li span { overflow-wrap: anywhere; min-width: 0; }
  .taste-settings :global(.settings-row) { grid-template-columns: minmax(0, 1fr) auto; }
  .taste-layers { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-6); }
  .taste-strength { display: grid; gap: var(--space-2); margin-block: var(--space-3); } .taste-strength strong { font-size: var(--text-sm); }
  summary { min-height: 44px; cursor: pointer; } .reset-nudges { display: flex; align-items: center; gap: var(--space-2); min-height: 44px; margin-top: var(--space-4); }
  .settings-toggle :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .settings-toggle :global([data-slot="checkbox"]:focus-visible) { outline: 2px solid var(--md-sys-color-primary); }
  .settings-toggle :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .settings-toggle :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  @media (max-width: 600px) { .taste-layers { grid-template-columns: minmax(0, 1fr); } }
</style>
