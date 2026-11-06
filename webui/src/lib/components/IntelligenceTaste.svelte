<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { taste, type SoundProfile, type TasteNudge, type TasteProfile } from "$lib/api";
  import { formatCount } from "$lib/intelligence";
  import { nudgeMarker, nudgeName, soundDifference, soundSummary, steeringNames, tasteSummary } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Popover } from "$lib/components/ui/popover";
  import { Progress } from "$lib/components/ui/progress";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  let profile = $state<TasteProfile | null>(null), loading = $state(true), error = $state("");
  let sound = $state<SoundProfile | null>(null);
  let action = $state(false), resetOpen = $state(false), clearNudges = $state(false), resetError = $state("");
  let selected = $state(""), duration = $state("today");
  let pending: Promise<void> | null = null, disposed = false;
  const layers = $derived(profile ? [{ title: "Lately", data: profile.lately }, { title: "Usually", data: profile.usually }] : []);
  // Skipped styles and artists are listed too, but they aren't what this listener is into.
  const liked = (rows: { name: string; value?: number }[] = []) => rows.filter(row => (row.value ?? 1) > 0).map(row => row.name);
  const tags = $derived(steeringNames(liked(profile?.lately.tags), liked(profile?.usually.tags), 12));
  const artists = $derived(steeringNames(liked(profile?.lately.artists), liked(profile?.usually.artists), 12));
  const summary = $derived(tasteSummary(liked(profile?.lately.tags), liked(profile?.usually.tags)));
  const summaryTags = $derived([...new Set(layers.flatMap(layer => layer.data.tags.map(row => row.name)))]);
  const summaryParts = $derived.by(() => {
    const pattern = summaryTags.map(name => name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|");
    return pattern ? summary.split(new RegExp(`(${pattern})`, "g")) : [summary];
  });
  async function load() {
    if (pending) return pending;
    if (!profile) loading = true;
    error = "";
    pending = (async () => {
      // The sound card is extra: it appears when AudioMuse has analyzed enough of the songs this listener plays.
      void taste.sound().then(next => { if (!disposed) sound = next.profile; }).catch(() => {});
      try { const next = await taste.get(); if (!disposed) { profile = next; error = ""; } }
      catch { if (!disposed) error = "Your taste could not be loaded."; }
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
        <SegmentedNav variant="segments" label={`Duration for ${name}`} items={[{ id: "today", label: "Today only" }, { id: "persistent", label: "Until I change it" }]} active={duration} onchange={value => duration = value} />
        <div class="nudge-options">{#each ["more", "less", "never"] as mode}<Button variant="ghost" disabled={action} onclick={() => nudge(kind, name, mode as TasteNudge["mode"])}>{mode === "more" ? "More" : mode === "less" ? "Less" : "Never"}</Button>{/each}</div>
      </Popover.Content></Popover.Portal>
    </Popover.Root>
  {/each}</div>
{/snippet}
<div class="your-taste">
  <div class="section-heading"><h2>Your taste</h2><RefreshButton variant="secondary" label="Refresh taste" disabled={action} onrefresh={load} /></div>
  <PageStatus items={[{ label: profile?.paused ? "Learning paused" : "Your listening shapes your mixes" }, { label: profile ? countLabel(profile.nudges.length, "active choice") : "Loading your taste" }]} />
  {#if error}<ErrorState title="Couldn't load your taste" detail={error} onretry={load} />{/if}
  {#if loading}<RowsSkeleton label="Loading your taste" />{:else if profile}
    <section class="settings-card summary"><p>{#each summaryParts as part}{#if summaryTags.includes(part)}<strong>{part}</strong>{:else}{part}{/if}{/each}</p><small>{profile.latelyWeight >= .5 ? "Today's listening counts most right now." : "Your usual taste counts most right now."}</small></section>
    {#if sound}
      <section class="settings-card taste-section sound-profile" aria-labelledby="sound-heading">
        <h2 id="sound-heading">Your sound</h2>
        <p class="sound-summary">{soundSummary(sound)}</p>
        <div class="sound-traits" role="list" aria-label="Compared with the rest of your library">{#each sound.traits as trait (trait.name)}
          <div class="sound-trait" role="listitem" aria-label={`${trait.name}: ${trait.difference === 0 ? "typical for your library" : `${soundDifference(trait.difference)} compared with your library`}`}>
            <strong>{trait.name}</strong>
            <span class="sound-track" aria-hidden="true"><span class="sound-fill" class:less={trait.difference < 0} style={`--extent: ${Math.min(100, Math.abs(trait.difference)) / 2}%`}></span></span>
            <small aria-hidden="true">{soundDifference(trait.difference)}</small>
          </div>
        {/each}</div>
        <small>Compared with {countLabel(sound.librarySongs, "other analyzed song")} in your library. From AudioMuse's analysis of {countLabel(sound.songs, "song")} you've finished{sound.skippedSongs ? ` and ${formatCount(sound.skippedSongs)} you mostly skip` : ""}.</small>
      </section>
    {/if}
    <section class="panel taste-section"><h2>Steer your mixes</h2><p>Tap a style to hear more or less of it.</p>{@render chips("tag", tags)}<h3>Artists</h3>{@render chips("artist", artists)}</section>
    <section class="panel taste-section" aria-label="Hidden"><h2>Hidden</h2>
      {#if profile.nudges.some(item => item.mode === "never")}<ul>{#each profile.nudges.filter(item => item.mode === "never") as item (`${item.kind}:${item.name}`)}<li><span>{nudgeName(item)}</span><Button variant="text" disabled={action} aria-label={`Undo ${nudgeName(item)}`} onclick={() => update(() => taste.remove(item.kind, item.name))}>Undo</Button></li>{/each}</ul>{:else}<p>Nothing hidden.</p>{/if}
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
  .sound-summary { margin-top: var(--space-2); }
  .sound-traits { display: grid; gap: var(--space-2); margin-block: var(--space-4); max-width: 40rem; }
  .sound-trait { display: grid; grid-template-columns: 7rem minmax(0, 1fr) 2.75rem; align-items: center; gap: var(--space-3); min-height: 28px; }
  .sound-trait strong { font-size: var(--text-sm); }
  .sound-trait small { text-align: end; font-variant-numeric: tabular-nums; }
  .sound-track { position: relative; height: 4px; border-radius: 999px; background: var(--md-sys-color-surface-container-highest); }
  .sound-track::after { content: ""; position: absolute; left: 50%; top: -5px; bottom: -5px; width: 2px; margin-left: -1px; border-radius: 1px; background: var(--md-sys-color-outline); }
  .sound-fill { position: absolute; top: 0; bottom: 0; left: 50%; width: var(--extent); border-radius: 0 999px 999px 0; background: var(--md-sys-color-primary); }
  .sound-fill.less { left: auto; right: 50%; border-radius: 999px 0 0 999px; }
  @media (max-width: 600px) { .taste-layers { grid-template-columns: minmax(0, 1fr); } }
</style>
