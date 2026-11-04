<script lang="ts">
  import { onMount } from "svelte";
  import { settings, setupAccess } from "$lib/api";
  import { takeRestartUndo, type RestartUndo } from "$lib/settings-restart";
  import { toast } from "$lib/toasts";
  import { savedBeforeRestart, waitForRestart } from "$lib/restart";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  let change = $state<RestartUndo | null>(null), open = $state(false), busy = $state(false), error = $state("");
  const abort = new AbortController();
  onMount(() => {
    change = takeRestartUndo();
    if (change) toast.success(`Saved · ${change.label}`, { label: "Undo", run: () => { open = true; } });
    return () => abort.abort();
  });
  async function undo() {
    if (!change || busy) return;
    busy = true; error = "";
    try {
      try {
        if (change.kind === "config") await settings.save({ [change.key]: change.value });
        else {
          const current = await setupAccess.oidc();
          await setupAccess.save("oidc", { ...change.settings, clientSecret: null, removeClientSecret: false, expectedRevision: current.settings.revision }, current.csrfToken);
        }
      } catch (cause) { if (!savedBeforeRestart(cause)) throw cause; }
      await waitForRestart(abort.signal); window.location.reload();
    } catch (cause) { error = cause instanceof Error ? cause.message : "The previous setting could not be restored. Try again."; }
    finally { busy = false; }
  }
</script>
<ConfirmDialog bind:open title="Restart Allstarr to undo?" description="Restoring the previous setting needs another restart. Music that is playing pauses briefly, and this page reconnects when Allstarr is ready." confirmLabel={busy ? "Restoring…" : "Restart and undo"} confirmVariant="default" closeOnConfirm={false} disabled={busy} {error} onConfirm={undo} />
