<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { ApiError, mixes, taste, type MixDetail, type MixSong } from "$lib/api";
  import { mixDescription, mixExplanation, mixName, mixNextStep } from "$lib/for-you";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import MixCover from "$lib/components/MixCover.svelte";
  import MixAppState from "$lib/components/MixAppState.svelte";
  import MixSongs from "$lib/components/MixSongs.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  let { id, administrator = false }: { id: string; administrator?: boolean } = $props();
  let detail = $state<MixDetail | null>(null), error = $state(""), busy = $state(false), throttled = $state(false);
  let deleteOpen = $state(false), aboutOpen = $state(false);
  let aboutAnchor = $state<HTMLButtonElement | null>(null);
  let disposed = false, request = 0, currentId = "", pending: Promise<void> | null = null;
  let throttleTimer: ReturnType<typeof setTimeout> | undefined;
  const nextStep = $derived(mixNextStep(detail?.mix.kind ?? "", detail?.mix.readiness.message ?? "", administrator));
  const close = () => { void goto("#/for-you", { keepFocus: true, noScroll: true }); };
  async function load() {
    const target = id;
    if (pending && currentId === target) return pending;
    const version = ++request; currentId = target;
    pending = (async () => {
      try {
        const next = await mixes.get(target);
        if (!disposed && version === request) { detail = next; error = ""; }
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
    <header><div><Dialog.Title>{mixName(detail?.mix.name ?? "Your mix")}</Dialog.Title><Dialog.Description>{detail?.mix.sourceLabel ? `From ${detail.mix.sourceLabel} · kept in sync` : detail ? mixDescription(detail.mix.description, detail.mix.kind) : "Songs picked for you"}</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close mix">×</Dialog.Close></header>
    <div class="mix-body">
      {#if error}<p role="alert">{error}</p><Button variant="secondary" onclick={load}>Try again</Button>{/if}
      {#if !detail && !error}<Skeleton class="skeleton-panel" aria-label="Loading mix" />{:else if detail}
        <div class="mix-heading"><div class="mix-cover"><MixCover mix={detail.mix} songs={detail.songs} /></div><p>{detail.mix.updatedAt ? `Updated ${new Date(detail.mix.updatedAt).toLocaleString(undefined, { weekday: "long", hour: "numeric", minute: "2-digit" })} · ` : ""}{detail.songs.length} songs</p></div>
        <div class="mix-actions">
          <MixAppState added={detail.mix.inApps} allowed={detail.mix.appsAllowed} {busy} onadd={() => void perform(target => mixes.add(target))} />
          {#if detail.mix.kind !== "custom" && detail.mix.kind !== "external"}<span title={throttled ? "Wait 15 minutes before refreshing again" : "Refresh this mix"}><RefreshButton label="Refresh mix" variant="secondary" disabled={busy || throttled} onrefresh={refresh} /></span>{/if}
          <DropdownMenu.Root><DropdownMenu.Trigger bind:ref={aboutAnchor} class="icon-button" aria-label="Mix actions"><UiIcon name="more" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="bits-menu mix-menu" align="end"><DropdownMenu.Item class="bits-menu-item" onSelect={() => setTimeout(() => { if (!disposed) aboutOpen = true; }, 0)}>About this mix</DropdownMenu.Item>{#if detail.mix.kind === "custom"}<DropdownMenu.Item class="bits-menu-item" onSelect={() => deleteOpen = true}>Delete mix</DropdownMenu.Item>{/if}</DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
        </div>
        <div class="mix-management">{#if detail.mix.inApps}<a href="#/library/playlists">Manage in Playlists</a>{/if}</div>
        {#if !detail.mix.appsAllowed}<p class="muted" id="mix-apps-hint">Your admin hasn’t allowed adding mixes to apps.</p>{/if}
        {#if !detail.mix.readiness.ready}<div class="settings-card explanation"><p>{detail.mix.readiness.message}</p><Button variant="secondary" href={nextStep.href}>{nextStep.label}</Button></div>
        {/if}
        {#if detail.songs.length}<MixSongs songs={detail.songs} {busy} onfeedback={detail.mix.kind === "external" ? undefined : feedback} />{/if}
        <Popover.Root bind:open={aboutOpen}><Popover.Portal><Popover.Content customAnchor={aboutAnchor} class="bits-menu mix-about" sideOffset={8} onCloseAutoFocus={event => { event.preventDefault(); aboutAnchor?.focus(); }}><p>{mixExplanation(detail.mix.kind)}</p><Popover.Close class="icon-button" aria-label="Close explanation">×</Popover.Close></Popover.Content></Popover.Portal></Popover.Root>
      {/if}
    </div>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>
<ConfirmDialog bind:open={deleteOpen} title="Delete mix?" description="This saved mix will be removed from your mixes and apps." confirmLabel="Delete mix" disabled={busy} closeOnConfirm={false} onConfirm={() => perform(async target => { await mixes.delete(target); if (id === target) close(); })} />
<style>
  :global(.mix-drawer) { left: auto; right: 0; top: 0; transform: none; width: min(560px, 100%); height: 100dvh; max-height: 100dvh; border-radius: 0; display: flex; flex-direction: column; overflow: hidden; }
  .mix-body { padding: var(--surface-gutter); overflow-y: auto; min-height: 0; }
  .mix-heading { display: flex; align-items: center; gap: var(--space-4); margin-bottom: var(--space-4); }
  .mix-cover { width: 160px; flex: 0 0 160px; }
  .mix-heading p, .muted { font-size: var(--text-sm); color: var(--color-ink-muted); }
  .mix-actions { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) var(--control-md); align-items: center; gap: var(--space-2); max-inline-size: calc(var(--control-lg) * 6 + var(--control-md) + var(--space-2) * 2); }
  .mix-actions > span { display: flex; align-items: center; min-inline-size: 0; }
  .mix-actions > :global(.icon-button) { grid-column: 3; }
  .mix-actions :global(.app-state) { inline-size: auto; min-inline-size: 0; }
  .mix-actions :global([data-slot="button"]) { inline-size: 100%; padding-inline: var(--space-2); white-space: normal; overflow-wrap: anywhere; }
  .mix-management { min-block-size: var(--control-md); font-size: var(--text-xs); }
  .mix-management a { display: inline-flex; align-items: center; min-block-size: var(--control-md); }
  .explanation { padding: var(--surface-gutter); display: grid; gap: var(--space-3); margin-top: var(--space-4); }
  :global(.mix-menu .bits-menu-item) { min-height: 44px; }
  :global(.mix-about) { display: flex; align-items: start; gap: var(--space-3); max-width: min(360px, calc(100vw - 32px)); padding: var(--space-4); }
  @media (max-width: 600px) { .mix-cover { width: 120px; flex-basis: 120px; } }
</style>
