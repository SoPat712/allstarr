<script lang="ts">
  import { onMount } from "svelte";
  import { taste, type TasteLayer, type TasteNudge, type TasteProfile } from "$lib/api";
  import { nudgeLabel, weightDescription } from "$lib/taste";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";

  let profile = $state<TasteProfile | null>(null), loading = $state(true), error = $state("");
  let action = $state(false), resetOpen = $state(false), clearNudges = $state(false), resetError = $state("");
  let pending: Promise<void> | null = null, disposed = false;
  const layers = $derived(profile ? [{ title: "Lately", data: profile.lately }, { title: "Usually", data: profile.usually }] : []);
  const hasTaste = $derived(layers.some(layer => layer.data.tags.length || layer.data.artists.length));

  function groups(layer: TasteLayer) {
    return [
      { title: "Top tags", kind: "tag" as const, rows: layer.tags.slice(0, 5) },
      { title: "Top artists", kind: "artist" as const, rows: layer.artists.slice(0, 5) },
    ];
  }

  async function load(showError = true) {
    if (pending) return pending;
    pending = (async () => {
      try {
        const next = await taste.get();
        if (!disposed) { profile = next; error = ""; }
      } catch { if (!disposed && showError) error = "Your taste could not be loaded. Refresh to try again."; }
      finally { if (!disposed) loading = false; pending = null; }
    })();
    return pending;
  }

  async function update(operation: () => Promise<TasteProfile>, resetting = false) {
    if (action) return;
    action = true;
    try {
      await pending;
      const next = await operation();
      if (!disposed) { profile = next; error = ""; if (resetting) resetOpen = false; }
    } catch {
      if (!disposed) {
        if (resetting) resetError = "Your taste could not be reset. Try again.";
        else toast.error("Your taste could not be updated. Try again.");
      }
    } finally { if (!disposed) action = false; }
  }

  function nudge(kind: TasteNudge["kind"], name: string, mode: TasteNudge["mode"], todayOnly = false) {
    return update(() => taste.nudge(kind, name, mode, todayOnly, Intl.DateTimeFormat().resolvedOptions().timeZone));
  }
  function openReset() { clearNudges = false; resetError = ""; resetOpen = true; }
  const scheduler = createRefreshScheduler(async () => { if (!action) await load(false); });
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); };
  });
</script>

{#snippet durationMenu(kind: TasteNudge["kind"], name: string, mode: "more" | "less")}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class={`${buttonVariants({ variant: "ghost" })} taste-nudge-trigger`} disabled={action} aria-label={`${mode === "more" ? "More" : "Less"} ${name}`}>
      {mode === "more" ? "+ More" : "− Less"}
    </DropdownMenu.Trigger>
    <DropdownMenu.Portal>
      <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
        <DropdownMenu.Item class="bits-menu-item taste-menu-item" onSelect={() => void nudge(kind, name, mode, true)}>Today only</DropdownMenu.Item>
        <DropdownMenu.Item class="bits-menu-item taste-menu-item" onSelect={() => void nudge(kind, name, mode)}>Until I change it</DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Portal>
  </DropdownMenu.Root>
{/snippet}

<div class="your-taste">
  <div class="section-heading">
    <p>Allstarr learns from songs you finish and skip. Use More and Less to steer radio and suggestions. Never hides something from suggestions.</p>
    <RefreshButton variant="secondary" label="Refresh taste" disabled={action} onrefresh={() => load()} />
  </div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading your taste" />
  {:else if profile}
    <p class="taste-weight">{weightDescription(profile.latelyWeight)}</p>
    {#if !hasTaste}
      <div class="settings-card taste-empty"><p>Play some music and your taste will show up here.</p></div>
    {:else}
      <div class="taste-layers">
        {#each layers as layer (layer.title)}
          <section class="panel taste-layer" aria-label={layer.title}>
            <h2>{layer.title}</h2>
            {#each groups(layer.data) as group (group.kind)}
              <div class="taste-group">
                <h3>{group.title}</h3>
                <ul>
                  {#each group.rows as row (row.name)}
                    <li class="taste-row">
                      <div class="taste-strength"><strong>{row.name}</strong><Progress value={Math.max(0, Math.min(100, row.strength))} aria-label={`${row.name} strength`} /></div>
                      <div class="taste-row-actions">
                        {@render durationMenu(group.kind, row.name, "more")}
                        {@render durationMenu(group.kind, row.name, "less")}
                        <DropdownMenu.Root>
                          <DropdownMenu.Trigger class="icon-button" disabled={action} aria-label={`Actions for ${row.name}`}><UiIcon name="more" /></DropdownMenu.Trigger>
                          <DropdownMenu.Portal>
                            <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                              <DropdownMenu.Item class="bits-menu-item taste-menu-item" onSelect={() => void nudge(group.kind, row.name, "never")}>Never suggest</DropdownMenu.Item>
                            </DropdownMenu.Content>
                          </DropdownMenu.Portal>
                        </DropdownMenu.Root>
                      </div>
                    </li>
                  {/each}
                </ul>
                {#if !group.rows.length}<p>No {group.kind === "tag" ? "tags" : "artists"} yet.</p>{/if}
              </div>
            {/each}
          </section>
        {/each}
      </div>
    {/if}
    <section class="panel taste-nudges" aria-label="Your nudges">
      <h2>Your nudges</h2>
      {#if profile.nudges.length}
        <ul>{#each profile.nudges as item (`${item.kind}:${item.name}`)}
          <li><span>{nudgeLabel(item)}</span><button class="icon-button" type="button" disabled={action} aria-label={`Remove ${nudgeLabel(item)}`} onclick={() => void update(() => taste.remove(item.kind, item.name))}>×</button></li>
        {/each}</ul>
      {:else}<p>No nudges yet.</p>{/if}
    </section>
    <div class="settings-card taste-settings">
      <label class="settings-row">
        <div><strong>Pause learning</strong><small>While paused, plays and skips don't change your taste.</small></div>
        <span class="settings-toggle"><Checkbox checked={profile.paused} disabled={action} aria-label="Pause learning" onCheckedChange={paused => void update(() => taste.pause(paused === true))} /></span>
      </label>
      <footer><Button variant="secondary" disabled={action} onclick={openReset}>Reset taste</Button></footer>
    </div>
  {/if}
</div>

<ConfirmDialog bind:open={resetOpen} title="Reset taste" description="Start learning your taste again from new plays and skips?" confirmLabel="Reset taste" disabled={action} closeOnConfirm={false} error={resetError} onConfirm={() => update(() => taste.reset(clearNudges), true)}>
  <label class="reset-nudges"><span class="settings-toggle"><Checkbox bind:checked={clearNudges} disabled={action} aria-label="Also remove my nudges" /></span><span>Also remove my nudges</span></label>
</ConfirmDialog>

<style>
  .your-taste { display: grid; min-width: 0; gap: var(--section-gap); }
  .section-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .section-heading p { flex: 1 1 28rem; }
  .section-heading p, .taste-weight, .taste-group > p, .taste-nudges > p, .taste-empty p { color: var(--color-ink-muted); }
  .taste-layers { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--section-gap); }
  .taste-layer, .taste-nudges, .taste-empty { padding: var(--surface-gutter); min-width: 0; }
  h2 { font-size: 1.125rem; font-weight: 600; margin-bottom: var(--space-4); }
  h3 { font-size: var(--text-sm); font-weight: 600; margin-bottom: var(--space-2); }
  .taste-group + .taste-group { margin-top: var(--space-6); }
  ul { list-style: none; margin: 0; padding: 0; }
  .taste-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: var(--space-2); align-items: center; padding-block: var(--space-2); }
  li + li { border-top: 1px solid var(--color-edge); }
  .taste-strength { min-width: 0; display: grid; gap: var(--space-2); }
  .taste-strength strong { overflow-wrap: anywhere; font-size: var(--text-sm); font-weight: 500; }
  .taste-row-actions { display: flex; align-items: center; }
  .taste-row-actions :global(.taste-nudge-trigger) { min-width: 44px; min-height: 44px; padding-inline: var(--space-2); font-size: var(--text-sm); }
  :global(.taste-menu-item) { min-height: 44px; }
  .taste-row-actions :global(.icon-button), .taste-nudges .icon-button { min-width: 44px; min-height: 44px; }
  .taste-nudges li { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); min-height: 44px; }
  .taste-nudges li span { min-width: 0; overflow-wrap: anywhere; }
  .taste-settings :global(.settings-row) { grid-template-columns: minmax(0, 1fr) auto; }
  .reset-nudges { display: flex; align-items: center; gap: var(--space-2); min-height: 44px; margin-top: var(--space-4); }
  .settings-toggle :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .settings-toggle :global([data-slot="checkbox"]:focus-visible) { outline: 2px solid var(--md-sys-color-primary); }
  .settings-toggle :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .settings-toggle :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  @media (max-width: 760px) { .taste-layers { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 480px) { .taste-row { grid-template-columns: minmax(0, 1fr); } .taste-row-actions { justify-self: end; } }
</style>
