<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Check, X } from "lucide-svelte";
  import { appleDownload, type AppleDownloadStatus } from "$lib/api";
  import { humanize } from "$lib/sources";

  let { open = $bindable(false) }: { open: boolean } = $props();

  let status = $state<AppleDownloadStatus | null>(null);
  let packageFile = $state<File | null>(null);
  let upload = $state<{ message?: string; fileName?: string; sizeBytes?: number } | null>(null);
  let username = $state("");
  let password = $state("");
  let code = $state("");
  let action = $state("");
  let feedback = $state("");
  let error = $state("");

  const loginState = $derived(
    String(status?.login_state || status?.account?.state || status?.state || "unknown")
      .trim().toLowerCase().replaceAll("-", "_").replaceAll(" ", "_"),
  );
  const awaiting2fa = $derived(
    ["awaiting_2fa", "awaiting2fa", "needs_2fa", "2fa_required", "two_factor_required"]
      .includes(loginState),
  );
  const gatewayReady = $derived(Boolean(
    status?.staged && status?.daemon_running && status?.wrapper_healthy,
  ));
  const packageReady = $derived(Boolean(status?.staged || upload));

  async function load() {
    action = "status";
    error = "";
    try {
      status = await appleDownload.status();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Apple download status is unavailable.";
    } finally {
      action = "";
    }
  }

  async function run(name: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = name;
    error = "";
    feedback = "";
    try {
      await operation();
      feedback = message;
      await load();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : `${message} failed.`;
    } finally {
      action = "";
    }
  }

  async function uploadPackage() {
    if (!packageFile) return;
    await run("upload", async () => {
      upload = await appleDownload.setup(packageFile!);
      packageFile = null;
    }, "Package staged. Run the host install command next.");
  }

  async function login() {
    await run("login", () => appleDownload.login(username, password), "Login submitted.");
    password = "";
  }

  async function submit2fa() {
    await run("2fa", () => appleDownload.submit2fa(code), "2FA submitted.");
    code = "";
  }

  $effect(() => {
    if (open) void load();
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog apple-download-dialog">
      <header>
        <div>
          <p class="eyebrow">Operator-managed Source</p>
          <Dialog.Title>Apple Music - Gamdl</Dialog.Title>
          <Dialog.Description>Install the gateway package, then authenticate its Apple Music session.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close Apple Music - Gamdl manager"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      <div class="apple-manager-body">
        <dl class="source-metrics">
          <div><dt>Gateway</dt><dd><span class={`status-pill ${gatewayReady ? "healthy" : "suggested"}`}>{gatewayReady ? "Ready" : humanize(status?.state ?? "unknown")}</span></dd></div>
          <div><dt>Session</dt><dd><span class={`status-pill ${status?.logged_in ? "healthy" : "suggested"}`}>{status?.logged_in ? "Authenticated" : humanize(loginState)}</span></dd></div>
          <div><dt>API contract</dt><dd>{status?.api_version || "Not discovered"}</dd></div>
          <div><dt>Provider</dt><dd><span class={`status-pill ${status?.ready ? "healthy" : "needs_config"}`}>{status?.ready ? "Playable" : "Needs setup"}</span></dd></div>
        </dl>

        <ol class="apple-setup-progress" aria-label="Apple download setup progress">
          <li class:complete={packageReady} class:active={!packageReady}>
            <span>{#if packageReady}<Check size={16} aria-hidden="true" />{:else}1{/if}</span><span><strong>Package</strong><small>{upload?.fileName || (packageReady ? "Installed" : "APK or APKM required")}</small></span>
          </li>
          <li class:complete={gatewayReady} class:active={packageReady && !gatewayReady}>
            <span>{#if gatewayReady}<Check size={16} aria-hidden="true" />{:else}2{/if}</span><span><strong>Gateway</strong><small>{gatewayReady ? "Running" : packageReady ? "Run host installer" : "Waiting for package"}</small></span>
          </li>
          <li class:complete={Boolean(status?.logged_in)} class:active={gatewayReady && !status?.logged_in}>
            <span>{#if status?.logged_in}<Check size={16} aria-hidden="true" />{:else}3{/if}</span><span><strong>Session</strong><small>{status?.logged_in ? "Authenticated" : gatewayReady ? "Login required" : "Waiting for gateway"}</small></span>
          </li>
        </ol>

        {#if !packageReady}
          <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void uploadPackage(); }}>
            <label class="field">
              <span>Apple Music package</span>
              <input type="file" accept=".apk,.apkm,application/vnd.android.package-archive" required onchange={(event) => packageFile = event.currentTarget.files?.[0] ?? null} />
              <small>Choose a legally obtained APK or APKM, up to 512 MB.</small>
            </label>
            <button class="button-primary" type="submit" disabled={!packageFile || Boolean(action)}>{action === "upload" ? "Uploading…" : "Upload package"}</button>
          </form>
        {:else if !gatewayReady}
          <section class="apple-host-action">
            <strong>{upload?.fileName || "Apple Music package"} is staged</strong>
            <p>Run this on the Docker host to verify the package, build the gateway, and start it.</p>
            <code>./allstarr.sh install-apple</code>
          </section>
        {:else if awaiting2fa}
          <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void submit2fa(); }}>
            <label class="field"><span>2FA code</span><input bind:value={code} inputmode="numeric" autocomplete="one-time-code" required /></label>
            <button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "2fa" ? "Submitting…" : "Submit 2FA"}</button>
          </form>
        {:else if !status?.logged_in}
          <form class="apple-manager-form" onsubmit={(event) => { event.preventDefault(); void login(); }}>
            <label class="field"><span>Apple ID</span><input bind:value={username} autocomplete="username" required /></label>
            <label class="field"><span>Password</span><input bind:value={password} type="password" autocomplete="current-password" required /></label>
            <button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "login" ? "Signing in…" : "Start login"}</button>
          </form>
        {:else}
          <div class="compact-empty"><strong>Apple Music - Gamdl is ready</strong><p>The gateway and saved Apple Music session are authenticated.</p></div>
        {/if}

        {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      </div>

      <footer class="apple-manager-footer">
        <a class="button-secondary" href="#/settings/general?provider=provider-apple-download" onclick={() => open = false}>Provider settings</a>
        <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void load()}>Refresh status</button>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
