<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { X } from "@lucide/svelte";
  import { appleDownload, ApiError, normalizeResponse, type AppleDownloadStatus } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { diagnosticFailureMessage } from "$lib/diagnostics";

  let { open = $bindable(false), onStatus }: { open: boolean; onStatus?: (status: AppleDownloadStatus) => void } = $props();
  let status = $state<AppleDownloadStatus | null>(null);
  let checked = $state(false);
  let packageFile = $state<File | null>(null);
  let upload = $state<{ message?: string; fileName?: string; sizeBytes?: number } | null>(null);
  let username = $state(""); let password = $state(""); let code = $state("");
  let action = $state(""); let error = $state(""); let advanced = $state(false);
  let request = 0;
  const loginState = $derived(String(status?.login_state || status?.account?.state || "").toLowerCase().replaceAll("-", "_"));
  const awaiting2fa = $derived(["awaiting_2fa", "awaiting2fa", "needs_2fa", "2fa_required", "two_factor_required"].includes(loginState));
  const gatewayReady = $derived(Boolean(status?.staged && status?.daemon_running && status?.wrapper_healthy));
  const packageReady = $derived(Boolean(status?.staged || upload));

  async function load() {
    const current = ++request;
    try {
      const result = await appleDownload.status();
      if (current !== request) return;
      status = result; checked = true; advanced = !result.staged || !result.daemon_running || !result.wrapper_healthy;
      onStatus?.(result);
    } catch {
      if (current !== request) return;
      checked = true; error = "Apple Music setup could not be checked. Refresh status and try again.";
    }
  }
  async function run(name: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = name; error = "";
    try {
      await operation(); await load();
      if ((name === "login" || name === "2fa") && status?.logged_in) {
        open = false; toast.success("Signed in to Apple Music");
      } else toast.info(message);
    } catch (cause) {
      const details = cause instanceof ApiError ? normalizeResponse(cause.details) as { error?: string } : {};
      error = details.error?.includes("account_mismatch") ? diagnosticFailureMessage({ failureCode: details.error })
        : "Apple Music could not complete this step. Check the details you entered and try again.";
    } finally { action = ""; password = ""; code = ""; }
  }
  async function uploadPackage() {
    if (!packageFile) return;
    await run("upload", async () => { upload = await appleDownload.setup(packageFile!); packageFile = null; }, "Package staged. Run the host install command next.");
  }
  $effect(() => {
    if (open) { checked = false; status = null; error = ""; void load(); }
    else { request++; password = ""; code = ""; }
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog apple-download-dialog">
      <header>
        <div>
          <Dialog.Title>Sign in to Apple Music</Dialog.Title>
          <Dialog.Description>Use the same Apple account as your Media User Token. This sign-in enables playback, downloads and synced lyrics.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close Apple Music sign-in"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <div class="apple-manager-body">
        {#if !checked}
          <p class="inline-status" role="status">Checking setup…</p>
        {:else}
          {#if gatewayReady}
            {#if awaiting2fa}
              <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void run("2fa", () => appleDownload.submit2fa(code), "2FA code submitted. Refresh status if sign-in is still pending."); }}>
                <label class="field"><span>2FA code</span><input bind:value={code} inputmode="numeric" autocomplete="one-time-code" required /></label>
                <Button type="submit" disabled={Boolean(action)}>Submit 2FA</Button>
              </form>
            {:else if !status?.logged_in}
              <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void run("login", () => appleDownload.login(username, password), "Sign-in submitted. Enter your 2FA code if requested."); }}>
                <label class="field"><span>Apple ID</span><input bind:value={username} autocomplete="username" required /></label>
                <label class="field"><span>Password</span><input bind:value={password} type="password" autocomplete="current-password" required /></label>
                <Button type="submit" disabled={Boolean(action)}>{action === "login" ? "Signing in…" : "Sign in"}</Button>
              </form>
            {:else}
              <div class="compact-empty"><strong>Signed in to Apple Music</strong><p>{status.account?.maskedIdentifier || "The playback session is ready."}</p></div>
            {/if}
          {/if}
          <details bind:open={advanced} class="apple-advanced">
            <summary>Advanced setup</summary>
            <p>Gateway: {gatewayReady ? "Running" : "Not set up"}. Package: {packageReady ? "Installed" : "Missing"}.</p>
            {#if !packageReady}
              <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void uploadPackage(); }}>
                <label class="field"><span>Apple Music package</span><input type="file" accept=".apk,.apkm,application/vnd.android.package-archive" required onchange={(event) => packageFile = event.currentTarget.files?.[0] ?? null} /><small>Choose a legally obtained APK or APKM, up to 512 MB.</small></label>
                <Button type="submit" disabled={!packageFile || Boolean(action)}>Upload package</Button>
              </form>
            {:else if !gatewayReady}
              <section class="apple-host-action"><strong>{upload?.fileName || "Apple Music package"} is staged</strong><p>Run this on the Docker host to verify the package, build the gateway, and start it.</p><code>./allstarr.sh install-apple</code></section>
            {:else}<p>The gateway is already installed. API contract: {status?.api_version || "Not discovered"}.</p>{/if}
          </details>
        {/if}
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      </div>
      <footer class="dialog-actions">
        <Button variant="secondary" href="#/integrations/services/apple-musickit" onclick={() => open = false}>Provider settings</Button>
        <Button variant="secondary" disabled={Boolean(action)} onclick={() => void load()}>Refresh status</Button>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
