<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, mixes, taste, type MixDetail, type MixSong } from "$lib/api";
  import { mixExplanation, mixName, mixNextStep } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import MixArtwork from "$lib/components/MixArtwork.svelte";
  import MixSongs from "$lib/components/MixSongs.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  let { id, administrator = false }: { id: string; administrator?: boolean } = $props();
  let detail = $state<MixDetail | null>(null), error = $state(""), busy = $state(false), throttled = $state(false);
  let published = $state<boolean | null>(null), deleteOpen = $state(false), aboutOpen = $state(false);
  let aboutAnchor = $state<HTMLButtonElement | null>(null);
  let disposed = false, request = 0, currentId = "", pending: Promise<void> | null = null;
  let throttleTimer: ReturnType<typeof setTimeout> | undefined;
  const nextStep = $derived(mixNextStep(detail?.mix.kind ?? "", detail?.mix.readiness.message ?? "", administrator));
  const close = () => { window.location.hash = "#/for-you"; };
  async function load() {
    const target = id;
    if (pending && currentId === target) return pending;
    const version = ++request; currentId = target;
    pending = (async () => {
      try {
        const [next, setting] = await Promise.all([mixes.get(target), administrator ? mixes.publishSetting() : Promise.resolve(null)]);
        if (!disposed && version === request) { detail = next; published = setting?.enabled ?? null; error = ""; }
      } catch { if (!disposed && version === request) error = "Couldn't load this mix. Try again."; }
      finally { if (version === request) pending = null; }
    })();
    return pending;
  }
  $effect(() => { id; detail = null; aboutOpen = false; deleteOpen = false; throttled = false; clearTimeout(throttleTimer); void load(); });
  async function perform(operation: (target: string) => Promise<unknown>) {
    const target = id;
    if (busy) return;
    busy = true;
    try { await pending; await operation(target); if (!disposed && id === target) await load(); }
    catch { if (!disposed) toast.error("Your mix could not be updated. Try again."); }
    finally { if (!disposed) busy = false; }
  }
  async function refresh() {
    await perform(async target => {
      try { await mixes.refresh(target); }
      catch (cause) {
        if (cause instanceof ApiError && cause.status === 429) {
          if (id === target && !disposed) {
            throttled = true;
            throttleTimer = setTimeout(() => { throttled = false; }, 15 * 60_000);
          }
          toast.info("Wait 15 minutes before refreshing this mix again.");
        } else throw cause;
      }
    });
  }
  async function feedback(row: MixSong, kind: "moreLikeThis" | "notForMe" | "neverArtist", today = false) {
    await perform(async target => {
      const before = await taste.get();
      await mixes.feedback(target, row.song.id, kind, today);
      if (disposed) return;
      if (id === target && detail) detail = { ...detail, songs: detail.songs.filter(item => kind === "neverArtist" ? item.song.artist !== row.song.artist : item.song.id !== row.song.id) };
      const after = await taste.get();
      const affected = kind === "notForMe" ? { kind: "song", name: row.song.canonicalRecordingId ?? row.song.id }
        : kind === "neverArtist" ? { kind: "artist", name: row.song.artist } : null;
      const changed = affected && after.nudges.find(item => item.kind === affected.kind && item.name.toLowerCase() === affected.name.toLowerCase());
      toast.success("Your mixes will follow your choice", changed ? { label: "Undo", run: async () => {
        try {
          const latest = (await taste.get()).nudges.find(item => item.kind === changed.kind && item.name.toLowerCase() === changed.name.toLowerCase());
          if (!latest || latest.mode !== changed.mode || latest.expiresAt !== changed.expiresAt) {
            toast.info("Your choice has changed since then. You can edit it in Your taste."); return;
          }
          await taste.remove(changed.kind, changed.name);
          const old = before.nudges.find(item => item.kind === changed.kind && item.name.toLowerCase() === changed.name.toLowerCase());
          if (old) await taste.nudge(old.kind, old.name, old.mode, Boolean(old.expiresAt), Intl.DateTimeFormat().resolvedOptions().timeZone);
          if (!disposed && id === target) await load();
        } catch { toast.error("Your choice could not be undone. Try Your taste."); }
      } } : undefined, 6000);
    });
  }
  const scheduler = createRefreshScheduler(async () => { if (!busy) await load(); });
  onMount(() => {
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; request++; clearTimeout(throttleTimer); unsubscribe(); scheduler.cancel(); };
  });
</script>
<Dialog.Root open={true} onOpenChange={open => { if (!open) close(); }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog mix-drawer">
    <header><div><Dialog.Title>{mixName(detail?.mix.name ?? "Your mix")}</Dialog.Title><Dialog.Description>{detail?.mix.sourceLabel ? `From ${detail.mix.sourceLabel} · kept in sync` : detail?.mix.description ?? "Songs picked for you"}</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close mix">×</Dialog.Close></header>
    <div class="mix-body">
      {#if error}<p role="alert">{error}</p><Button variant="secondary" onclick={load}>Try again</Button>{/if}
      {#if !detail && !error}<Skeleton class="skeleton-panel" aria-label="Loading mix" />{:else if detail}
        <div class="mix-heading"><div class="mix-cover"><MixArtwork references={detail.mix.coverArtwork} /></div><p>{detail.mix.updatedAt ? `Updated ${new Date(detail.mix.updatedAt).toLocaleString(undefined, { weekday: "long", hour: "numeric", minute: "2-digit" })} · ` : ""}{detail.songs.length} songs</p></div>
        <div class="mix-actions">
          <label class="app-switch"><span class="settings-toggle"><Checkbox checked={detail.mix.showInApps} disabled={busy || published === false} aria-label="Show in my apps" onCheckedChange={enabled => void perform(target => mixes.inApps(target, enabled === true))} /></span><span>Show in my apps</span></label>
          {#if detail.mix.kind !== "custom" && detail.mix.kind !== "external"}<span title={throttled ? "Wait 15 minutes before refreshing again" : "Refresh this mix"}><RefreshButton label="Refresh mix" variant="secondary" disabled={busy || throttled} onrefresh={refresh} /></span>{/if}
          <DropdownMenu.Root><DropdownMenu.Trigger bind:ref={aboutAnchor} class="icon-button" aria-label="Mix actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="bits-menu mix-menu" align="end"><DropdownMenu.Item class="bits-menu-item" onSelect={() => setTimeout(() => { if (!disposed) aboutOpen = true; }, 0)}>About this mix</DropdownMenu.Item>{#if detail.mix.kind === "custom"}<DropdownMenu.Item class="bits-menu-item" onSelect={() => deleteOpen = true}>Delete mix</DropdownMenu.Item>{/if}</DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
        </div>
        {#if published === false}<p class="muted">Not published yet</p>{/if}
        {#if !detail.mix.readiness.ready}<div class="settings-card explanation"><p>{detail.mix.readiness.message}</p><Button variant="secondary" href={nextStep.href}>{nextStep.label}</Button></div>
        {:else}<MixSongs songs={detail.songs} {busy} onfeedback={detail.mix.kind === "external" ? undefined : feedback} />{/if}
        <Popover.Root bind:open={aboutOpen}><Popover.Portal><Popover.Content customAnchor={aboutAnchor} class="bits-menu mix-about" sideOffset={8} onCloseAutoFocus={event => { event.preventDefault(); aboutAnchor?.focus(); }}><p>{mixExplanation(detail.mix.kind)}</p><Popover.Close class="icon-button" aria-label="Close explanation">×</Popover.Close></Popover.Content></Popover.Portal></Popover.Root>
      {/if}
    </div>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>
<ConfirmDialog bind:open={deleteOpen} title="Delete mix?" description="This saved mix will be removed from your mixes and apps." confirmLabel="Delete mix" disabled={busy} closeOnConfirm={false} onConfirm={() => perform(async target => { await mixes.remove(target); if (id === target) close(); })} />
<style>
  :global(.mix-drawer) { left: auto; right: 0; top: 0; transform: none; width: min(560px, 100%); height: 100dvh; max-height: 100dvh; border-radius: 0; display: flex; flex-direction: column; overflow: hidden; }
  .mix-body { padding: var(--surface-gutter); overflow-y: auto; min-height: 0; }
  .mix-heading { display: flex; align-items: center; gap: var(--space-4); margin-bottom: var(--space-4); }
  .mix-cover { width: 160px; flex: 0 0 160px; }
  .mix-heading p, .muted { font-size: var(--text-sm); color: var(--color-ink-muted); }
  .mix-actions { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-3); }
  .app-switch { display: flex; align-items: center; gap: var(--space-3); min-height: 44px; }
  .settings-toggle :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .settings-toggle :global([data-slot="checkbox"]:focus-visible) { outline: 2px solid var(--md-sys-color-primary); }
  .settings-toggle :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .settings-toggle :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  .explanation { padding: var(--surface-gutter); display: grid; gap: var(--space-3); margin-top: var(--space-4); }
  :global(.mix-menu .bits-menu-item) { min-height: 44px; }
  :global(.mix-about) { display: flex; align-items: start; gap: var(--space-3); max-width: min(360px, calc(100vw - 32px)); padding: var(--space-4); }
  @media (max-width: 600px) { .mix-cover { width: 120px; flex-basis: 120px; } }
</style>
