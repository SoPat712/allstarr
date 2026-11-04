<script lang="ts">
  import { onMount } from "svelte";
  import { Lock } from "@lucide/svelte";
  import { settings } from "$lib/api";
  import { pathValue } from "$lib/settings";
  import { Button } from "$lib/components/ui/button";
  import AccessSettingsCard from "$lib/components/AccessSettingsCard.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  let config = $state<Record<string, unknown> | null>(null), error = $state(""), message = $state(""), busy = $state(false), passed = $state(false);
  const backend = $derived(String(config?.backendType ?? "Jellyfin"));
  const address = $derived(String(config ? pathValue(config, `${backend.toLowerCase()}.url`) ?? "Not set" : ""));
  async function load() {
    try { config = await settings.config(); error = ""; }
    catch (cause) { error = cause instanceof Error ? cause.message : "The saved server connection could not be loaded."; }
  }
  async function test() {
    if (busy) return;
    busy = true; error = ""; message = "";
    try { const result = await settings.mediaProbe(); passed = result.success; message = result.message; }
    catch (cause) { error = cause instanceof Error ? cause.message : "The server check could not finish. Try again."; }
    finally { busy = false; }
  }
  onMount(() => { void load(); });
</script>
<div class="settings-stack">
  {#if error}<ErrorState title="The server connection needs attention" detail={error} onretry={load} />{/if}
  {#if !config && !error}<RowsSkeleton count={2} />{:else if config}
    <section class="settings-card" aria-label="Media-server connection">
      <header><h3>{backend}</h3></header>
      <div class="settings-row"><div><strong>Server address</strong><small>The address Allstarr uses inside your network.</small></div><div class="server-address"><output>{address}</output><small><Lock size={12} aria-hidden="true" />Saved server connection</small></div></div>
      <div class="settings-row"><div><strong>Test the saved connection</strong><small>Checks song details, artwork and playback access.</small></div><Button variant="outline" disabled={busy} onclick={test}>{busy ? "Testing…" : "Test"}</Button></div>
      {#if message}<p class="settings-impact" role="status" class:passed>{message}</p>{/if}
    </section>
    <AccessSettingsCard kind="backend" />
  {/if}
</div>
<style>
  .server-address { display: grid; justify-items: end; gap: var(--space-1); min-width: 0; }
  .server-address small { display: flex; gap: var(--space-1); align-items: center; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .server-address output { overflow-wrap: anywhere; }
  .passed { color: var(--success); }
  @media (max-width: 650px) { .server-address { justify-items: start; } }
</style>
