<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Copy } from "@lucide/svelte";
  import { toast } from "$lib/toasts";
  import { listenerSupport, musicClientUrl, type ListenerContext } from "$lib/support";

  // What a music app asks for when it is added: the server address and the listener's username. An administrator
  // sees it beside the household's address setting, so theirs follows that setting; `revision` changes when it is saved.
  let { administrator = false, revision = undefined }: { administrator?: boolean; revision?: unknown } = $props();
  let context = $state<ListenerContext | null>(null);
  let address = $state("");
  let addressField = $state<HTMLInputElement>();
  let usernameField = $state<HTMLInputElement>();
  const usableAddress = $derived(musicClientUrl(address));
  const kind = $derived(context?.backend === "Subsonic" ? "Subsonic" : "Jellyfin");
  const unset = $derived(context?.addressSource === "unconfigured");

  async function copy(value: string, input?: HTMLInputElement) {
    if (!value) return;
    try {
      if (!navigator.clipboard?.writeText) throw new Error();
      await navigator.clipboard.writeText(value);
      toast.success("Copied.");
    } catch {
      input?.focus();
      input?.select();
      toast.info("Select and copy the highlighted text.");
    }
  }
  async function load() {
    // The card is a convenience on a settings page: without its details it stays away rather than show an error.
    try {
      const next = await listenerSupport.context();
      // An address a listener typed for themselves stays while the household has none.
      if (!context || next.musicClientUrl || administrator) address = next.musicClientUrl ?? "";
      context = next;
    } catch { context = null; }
  }
  onMount(() => void load());
  let loaded = false;
  $effect(() => {
    void revision;
    if (loaded) void load();
    loaded = true;
  });
</script>

{#if context}
  <section class="settings-card connect-card" aria-labelledby="connect-card-title">
    <header><div><h3 id="connect-card-title">Connect a music app</h3>
      <small>Add a {kind} server in your music app with this address, your username and your usual media-server password.</small></div></header>
    {#if unset}
      <p class="notice-info" role="status">{administrator
        ? "Set the music-client address above. Everyone copies it from their own settings."
        : "No address has been set for music apps. Enter the one your administrator gave you; it only changes what you copy."}</p>
    {/if}
    <div class="settings-row">
      <div><label for="connect-address"><strong>Server address</strong></label></div>
      <span class="connect-value">
        <input id="connect-address" bind:this={addressField} type="url" spellcheck="false" autocomplete="off" bind:value={address} readonly={!unset || administrator} placeholder={administrator ? "" : "https://music.example.com"} />
        <Button variant="secondary" disabled={!usableAddress} aria-label="Copy server address" onclick={() => void copy(usableAddress!, addressField)}><Copy size={16} aria-hidden="true" />Copy</Button>
      </span>
    </div>
    {#if address && !usableAddress}<p class="notice-info" role="status">Use an HTTP or HTTPS address without sign-in details, a query or a fragment.</p>{/if}
    <div class="settings-row">
      <div><label for="connect-username"><strong>Username</strong></label></div>
      <span class="connect-value">
        <input id="connect-username" bind:this={usernameField} value={context.username} readonly />
        <Button variant="secondary" aria-label="Copy username" onclick={() => void copy(context!.username, usernameField)}><Copy size={16} aria-hidden="true" />Copy</Button>
      </span>
    </div>
    {#if kind === "Jellyfin"}
      <footer><Button variant="outline" href={usableAddress ? `${usableAddress}/web/` : undefined} disabled={!usableAddress} target="_blank" rel="noopener noreferrer">Open web player</Button></footer>
    {/if}
  </section>
{/if}

<style>
  /* A whole address should be readable beside its Copy button. */
  @media (min-width: 651px) {
    :global(.settings-surface) .connect-card .settings-row { grid-template-columns: minmax(0, 1fr) minmax(10rem, 24rem); }
  }
  .connect-value { display: flex; min-width: 0; align-items: center; gap: var(--space-2); }
  .connect-value input { flex: 1; min-width: 0; min-height: var(--control-md); padding-inline: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel-raised); color: var(--color-ink); }
  .connect-card footer { display: flex; justify-content: flex-end; }
</style>
