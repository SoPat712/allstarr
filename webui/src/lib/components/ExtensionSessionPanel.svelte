<script lang="ts">
  import { tick, untrack } from "svelte";
  import { ExternalLink } from "@lucide/svelte";
  import { ApiError, extensions, type ExtensionPackage, type ExtensionSession } from "$lib/api";
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { sessionReasonText, sessionSummary, verificationHost } from "$lib/extensions";

  let { item, onchanged }: { item: ExtensionPackage; onchanged: () => Promise<void> } = $props();

  let session = $state<ExtensionSession | null>(null);
  let loadFailed = $state(false);
  let busy = $state<"" | "start" | "grant" | "clear">("");
  let grant = $state("");
  let message = $state("");
  let problem = $state("");
  let confirmOpen = $state(false);
  let startButton = $state<HTMLElement | null>(null);
  let verificationLink = $state<HTMLElement | null>(null);
  let signOutButton = $state<HTMLElement | null>(null);

  const titleId = $derived(`extension-session-${item.id}`);
  const grantId = $derived(`extension-session-grant-${item.id}`);
  const summary = $derived(session ? sessionSummary(session) : null);
  const host = $derived(verificationHost(session?.verificationUrl));
  const packageKey = $derived(`${item.id}:${item.revision}`);

  $effect(() => {
    void packageKey;
    untrack(() => void load(item));
  });

  async function load(target: ExtensionPackage) {
    try {
      session = await extensions.session(target);
      loadFailed = false;
    } catch {
      loadFailed = true;
    }
  }

  async function act(kind: "start" | "grant" | "clear", operation: () => Promise<ExtensionSession>, success: (next: ExtensionSession) => string) {
    if (busy) return false;
    const before = session?.state;
    busy = kind;
    message = "";
    problem = "";
    try {
      const next = await operation();
      session = next;
      if (next.reasonCode) problem = sessionReasonText(next.reasonCode);
      else message = success(next);
      await onchanged();
      return !next.reasonCode;
    } catch (cause) {
      if (cause instanceof ApiError && [404, 409].includes(cause.status)) {
        problem = "This extension changed while you were signing in. The latest version is shown; start sign-in again if it's still needed.";
        await onchanged();
      } else {
        problem = cause instanceof Error ? cause.message : "Sign-in could not be updated. Try again.";
      }
      return false;
    } finally {
      busy = "";
      if (session?.state !== before) await focusCurrentStep();
    }
  }

  async function focusCurrentStep() {
    await tick();
    (session?.state === "verification_pending" ? verificationLink ?? startButton
      : session?.state === "signed_in" ? signOutButton
        : startButton)?.focus();
  }

  function start() {
    return act("start", () => extensions.startSession(item), (next) =>
      next.state === "signed_in" ? "Sign-in restored." : "Verification started. Open the verification page to continue.");
  }

  async function complete(event: SubmitEvent) {
    event.preventDefault();
    const value = grant.trim();
    if (!value) {
      message = "";
      problem = sessionReasonText("grant_required");
      return;
    }
    if (await act("grant", () => extensions.completeSession(item, value), () => "Signed in.")) grant = "";
  }

  function cancel() {
    grant = "";
    return act("clear", () => extensions.clearSession(item), () => "Sign-in cancelled.");
  }

  async function signOut() {
    if (await act("clear", () => extensions.clearSession(item), () => "Signed out.")) confirmOpen = false;
  }
</script>

<section class="extension-session" aria-labelledby={titleId} aria-busy={busy ? "true" : undefined}>
  <header>
    <span>
      <strong id={titleId}>Sign-in</strong>
      <small>{summary ? summary.detail : loadFailed ? "Sign-in status is unavailable right now." : "Checking sign-in…"}</small>
    </span>
    {#if summary}<Badge state={summary.badge}>{summary.label}</Badge>{/if}
  </header>
  {#if problem}<p class="notice-error" role="alert">{problem}</p>{:else if message}<p class="extension-session-message" role="status">{message}</p>{/if}

  {#if loadFailed && !session}
    <div class="extension-session-actions"><Button size="sm" variant="secondary" onclick={() => void load(item)}>Check again</Button></div>
  {:else if session?.state === "verification_pending"}
    <ol class="extension-session-steps">
      <li>
        {#if host}
          <Button bind:ref={verificationLink} size="sm" href={session.verificationUrl ?? undefined} target="_blank" rel="noopener noreferrer">Open verification page<ExternalLink aria-hidden="true" /></Button>
          <small>Opens {host} in a new tab.</small>
        {:else}
          <small>The verification page isn't available. Start again to get a new link.</small>
        {/if}
      </li>
      <li>
        <form onsubmit={(event) => void complete(event)}>
          <label for={grantId}>Sign-in link or code</label>
          <input id={grantId} bind:value={grant} autocomplete="off" autocapitalize="off" spellcheck="false" maxlength="16384" placeholder="Paste what the verification page shows" />
          <Button type="submit" size="sm" disabled={Boolean(busy)}>{busy === "grant" ? "Completing…" : "Complete sign-in"}</Button>
        </form>
      </li>
    </ol>
    <div class="extension-session-actions">
      <Button bind:ref={startButton} size="sm" variant="secondary" disabled={Boolean(busy)} onclick={() => void start()}>{busy === "start" ? "Starting…" : "Get a new link"}</Button>
      <Button size="sm" variant="outline" disabled={Boolean(busy)} onclick={() => void cancel()}>{busy === "clear" ? "Cancelling…" : "Cancel sign-in"}</Button>
    </div>
  {:else if session?.state === "signed_in"}
    <div class="extension-session-actions">
      <Button bind:ref={signOutButton} size="sm" variant="secondary" disabled={Boolean(busy)} onclick={() => { problem = ""; confirmOpen = true; }}>Sign out</Button>
    </div>
  {:else if session}
    <div class="extension-session-actions">
      <Button bind:ref={startButton} size="sm" disabled={Boolean(busy)} onclick={() => void start()}>{busy === "start" ? "Starting…" : session.state === "expired" ? "Reconnect" : "Start verification"}</Button>
    </div>
  {/if}
</section>

<AlertDialog.Root bind:open={confirmOpen}>
  <AlertDialog.Portal><AlertDialog.Overlay class="dialog-overlay" /><AlertDialog.Content class="confirm-dialog">
    <AlertDialog.Title>Sign out of {item.displayName}?</AlertDialog.Title>
    <AlertDialog.Description>The extension stops using its service until you complete verification again. Its installation, permissions and Source accounts stay as they are.</AlertDialog.Description>
    {#if problem}<p class="notice-error" role="alert">{problem}</p>{/if}
    <footer class="dialog-actions"><AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} disabled={Boolean(busy)}>Cancel</AlertDialog.Cancel><Button variant="destructive" disabled={Boolean(busy)} onclick={() => void signOut()}>{busy === "clear" ? "Signing out…" : "Sign out"}</Button></footer>
  </AlertDialog.Content></AlertDialog.Portal>
</AlertDialog.Root>
