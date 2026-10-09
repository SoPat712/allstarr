<script lang="ts">
  import { KeyRound, X } from "@lucide/svelte";
  import { auth, playlistLinks, type MediaTarget } from "$lib/api";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";

  let {
    open = $bindable(false),
    target,
    onConnected,
  }: {
    open: boolean;
    target: MediaTarget | null;
    onConnected: (message: string, targetId: string) => void | Promise<void>;
  } = $props();

  let password = $state("");
  let saving = $state(false);
  let prepared = $state(false);
  let error = $state("");

  $effect(() => {
    if (!open) {
      prepared = false;
      password = "";
      return;
    }
    if (prepared) return;
    prepared = true;
    error = "";
  });

  async function save() {
    if (!target || saving || !password) return;
    saving = true;
    error = "";
    try {
      await auth.grantPlaylistConsent(password);
      password = "";
      const refreshed = await playlistLinks.targets();
      const currentTarget = refreshed.targets.find((item) => item.id === target.id) ??
        refreshed.targets.find((item) => item.protocol === "subsonic" && item.backendInstanceId === target.backendInstanceId);
      let queued = true;
      try {
        if (!currentTarget?.credentialReferenceId) throw new Error("Playlist access is not ready yet.");
        await playlistLinks.enqueueLibraryIndex(currentTarget.credentialReferenceId);
      } catch {
        queued = false;
      }
      open = false;
      await onConnected(
        queued
          ? "Library access connected. Indexing started."
          : "Library access connected. Automatic indexing will start shortly.",
        target.id,
      );
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Library access could not be connected.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog library-access-dialog">
      <header>
        <div>
          <p class="eyebrow">Private library access</p>
          <Dialog.Title>Connect Subsonic for background features</Dialog.Title>
          <Dialog.Description>Allow Allstarr to index your library and create recommendation playlists when you are not signed in.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close library access"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <form class="library-access-form" onsubmit={(event) => { event.preventDefault(); void save(); }}>
        <div class="library-access-copy">
          <span aria-hidden="true"><KeyRound size={20} /></span>
          <p><strong>Only your Allstarr account can use this credential.</strong><small>It is encrypted, bound to this exact Subsonic identity, and never included in job payloads or logs.</small></p>
        </div>
        <p class="identity-summary"><span>Signed-in listener</span><strong>{target?.displayName ?? "Current listener"}</strong></p>
        <label class="field"><span>Subsonic password</span><input bind:value={password} type="password" autocomplete="current-password" required maxlength="2000" /></label>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        <footer class="library-access-actions">
          <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
          <Button type="submit" disabled={saving || !password}>{saving ? "Connecting…" : target?.credentialReferenceId ? "Update access" : "Allow and index"}</Button>
        </footer>
      </form>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  :global(.library-access-dialog){--dialog-width:34rem}.library-access-form{display:grid;gap:1rem;padding:1rem}.library-access-copy{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:start;gap:.75rem;border-radius:var(--radius-md);background:var(--color-panel-raised);padding:.8rem}.library-access-copy>span{display:grid;place-items:center;width:2.5rem;height:2.5rem;border-radius:50%;background:var(--color-signal-muted);color:var(--color-signal-text)}.library-access-copy p,.library-access-copy strong,.library-access-copy small{display:block;margin:0}.library-access-copy small{margin-top:.2rem;color:var(--color-ink-muted)}.identity-summary{display:grid;gap:.2rem;margin:0}.identity-summary span{color:var(--color-ink-muted);font-size:.82rem}.library-access-actions{display:flex;justify-content:flex-end;gap:.5rem;border-top:1px solid var(--color-edge);padding-top:1rem}@media(max-width:620px){.library-access-actions,.library-access-actions>:global([data-slot="button"]){width:100%}.library-access-actions{display:grid}}
</style>
