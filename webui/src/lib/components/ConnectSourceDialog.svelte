<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { X } from "@lucide/svelte";
  import { sources, type ProviderAccount, type ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { supportedAudiences, sharingDescription, accountSubtitle, accountSettings, secretFromForm, settingDefault, sourceOriginLabel } from "$lib/sources";

  let {
    open = $bindable(false),
    providers,
    administrator,
    currentUserId = "",
    testConnection = false,
    account = null,
    users = [],
    initialProviderId = "",
    onSaved,
  }: {
    open: boolean;
    providers: ProviderDefinition[];
    administrator: boolean;
    currentUserId?: string;
    testConnection?: boolean;
    account?: ProviderAccount | null;
    users?: { id: string; displayName: string }[];
    initialProviderId?: string;
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let providerId = $state("");
  let step = $state(1);
  let scope = $state<"Personal" | "Shared">("Personal");
  let ownerUserId = $state("");
  let saving = $state(false);
  let error = $state("");
  let initialized = $state(false);

  let choices = $state<ProviderDefinition[]>([]);
  // One plain line per service, in the order listeners care about; internal categories such as health are left out.
  const categoryWords: [string, string][] = [["streaming", "playback"], ["download", "downloads"], ["lyrics", "lyrics"],
    ["playlists", "playlists"], ["playlist", "playlists"], ["scrobbling", "listens"], ["metadata", "song details"]];
  function whatItDoes(categories: string[]) {
    const words = [...new Set(categoryWords.filter(([category]) => categories.includes(category)).map(([, word]) => word))];
    const text = words.join(", ");
    return text ? text[0].toUpperCase() + text.slice(1) : "Your account and library";
  }

  function tileKey(event: KeyboardEvent, index: number) {
    const grid = (event.currentTarget as HTMLElement).parentElement;
    const columns = grid ? getComputedStyle(grid).gridTemplateColumns.split(" ").length : 1;
    const next = event.key === "ArrowRight" ? index + 1 : event.key === "ArrowLeft" ? index - 1 : event.key === "ArrowDown" ? index + columns : event.key === "ArrowUp" ? index - columns : event.key === "Home" ? 0 : event.key === "End" ? choices.length - 1 : null;
    if (next === null) return;
    event.preventDefault();
    const position = Math.max(0, Math.min(choices.length - 1, next));
    providerId = choices[position].id;
    (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLButtonElement>("button")[position]?.focus();
  }
  const selected = $derived(
    choices.find((provider) => provider.id === (account?.providerId ?? providerId)) ?? choices[0],
  );

  const currentSetting = (key: string, fallback: unknown) =>
    account?.configuration?.[key] ?? fallback;

  $effect(() => {
    if (!open) {
      choices = providers.filter((provider) => accountSettings(provider).length);
      initialized = false; error = ""; scope = "Personal"; ownerUserId = ""; step = 1;
    } else if (!initialized) {
      choices = providers.filter((provider) => accountSettings(provider).length);
      if (choices[0]) {
        initialized = true;
        providerId = account?.providerId || initialProviderId || choices[0].id;
        step = account || initialProviderId ? 2 : 1;
        ownerUserId = currentUserId;
      }
    }
  });

  $effect(() => {
    if (!open || account || !selected) return;
    const allowed = supportedAudiences(selected).filter((value) => administrator || value === "Personal");
    if (!allowed.includes(scope) && allowed[0]) scope = allowed[0];
  });

  async function create(event: SubmitEvent) {
    event.preventDefault();
    if (!selected || saving) return;
    saving = true;
    error = "";
    const form = event.currentTarget as HTMLFormElement;
    const data = new FormData(form);
    const secret = Object.fromEntries(Object.entries(secretFromForm(selected, data))
      .filter(([key, value]) => value !== "" || !accountSettings(selected).some(field => field.key === key && field.sensitive)));
    try {
      if (!account && scope === "Shared" && !data.has("confirmShare"))
        throw new Error("Confirm that household users may use this connection.");
      let saved: ProviderAccount;
      if (account) {
        const replacement = await sources.replaceSecret(account, secret);
        saved = account.enabled
          ? { ...account, revision: replacement.revision }
          : await sources.setEnabled({ ...account, revision: replacement.revision }, true);
      } else {
        saved = await sources.create({
          providerId: selected.id,
          displayName: String(data.get("displayName") || "").trim(),
          scope,
          ...(administrator && scope === "Personal" && ownerUserId ? { ownerUserId } : {}),
          enabled: true,
          secret,
        });
      }
      if (selected.id === "lastfm")
        await sources.authenticateLastFm(
          saved.id,
          String(data.get("username") || ""),
          String(data.get("password") || ""),
        );
      const savedState = !account ? "connected" : account.enabled ? "configuration saved" : "connection enabled";
      if (!testConnection) {
        await onSaved(`${selected.name} ${savedState}.`);
      } else try {
        await sources.test(saved);
        await onSaved(`${selected.name} ${savedState} and tested.`);
      } catch {
        await onSaved(`${selected.name} ${savedState}. Its connection test needs attention.`);
      }
      form.reset();
      open = false;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The Source could not be connected.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog connect-source-dialog">
      {#if !account}<ol class="connection-steps" aria-label="Connection steps"><li aria-current={step === 1 ? "step" : undefined}><span>{step > 1 ? "✓" : "1"}</span>Service</li><li aria-current={step === 2 ? "step" : undefined}><span>2</span>Sign in</li></ol>{/if}
      <header>
        <div>
          <Dialog.Title>{account ? `Configure ${selected?.name ?? "service"}` : step === 1 ? "Connect an account" : `Connect ${selected?.name ?? "an account"}`}</Dialog.Title>
          <Dialog.Description>{account
            ? "Update the saved sign-in. Leave stored secrets blank to keep them."
            : step === 1 ? "Pick the service. You’ll sign in on the next step." : "Your sign-in is encrypted and never shown again after you save it."}</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close source connection dialog"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>


      {#if !account && step === 1}
        <div class="connection-tiles" role="radiogroup" aria-label="Service">
          {#each choices as choice, index}
            <button class="connection-tile" type="button" role="radio" aria-checked={providerId === choice.id} tabindex={providerId === choice.id ? 0 : -1} onkeydown={event => tileKey(event, index)} onclick={() => providerId = choice.id}><ProviderMark id={choice.id} definition={choice} decorative /><strong>{choice.name}</strong><small>{sourceOriginLabel(choice)}</small><small>{whatItDoes(choice.categories ?? [])}</small></button>
          {/each}
        </div>
        <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "text" })}>Cancel</Dialog.Close><Button disabled={!selected} onclick={() => step = 2}>Next</Button></footer>
      {:else if selected}
        <form onsubmit={create}>
          {#if account}
            <div class="provider-select source-dialog-provider">
              <ProviderMark id={selected.id} definition={selected} decorative />
              <span><strong>{selected.name}</strong><small>{accountSubtitle(account)}</small></span>
            </div>
          {:else}
            <div class="provider-select source-dialog-provider"><ProviderMark id={selected.id} definition={selected} decorative /><strong>{selected.name}</strong></div>
          {/if}

          <div class="source-setting-grid">
            {#each accountSettings(selected) as field}
              <label class="field" class:wide={accountSettings(selected).length === 1}>
                <span>{field.label}</span>
                {#if field.type === "select"}
                  <SelectField name={field.key} label={field.label} value={String(currentSetting(field.key, settingDefault(field)))} options={field.options ?? []} required={field.required} />
                {:else if field.type === "toggle"}
                  <Checkbox name={field.key} checked={currentSetting(field.key, settingDefault(field)) === true} />
                {:else if field.sensitive && "multiline" in field && field.multiline === true}
                  <textarea
                    class="account-input"
                    name={field.key}
                    rows={6}
                    required={field.required && !account?.configuredFields?.includes(field.key)}
                    autocomplete="off"
                    autocapitalize="off"
                    spellcheck="false"
                    wrap="off"
                    placeholder={"placeholder" in field && typeof field.placeholder === "string" ? field.placeholder : "Paste the Debug Info here"}
                  ></textarea>
                {:else}
                  <input
                    class="account-input"
                    name={field.key}
                    type={field.sensitive ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
                    value={field.sensitive ? "" : String(currentSetting(field.key, settingDefault(field)))}
                    required={field.required && (!field.sensitive || !account?.configuredFields?.includes(field.key))}
                    autocomplete={field.key === "username" ? "username" : field.key === "password" ? "current-password" : "off"}
                  />
                {/if}
                {#if field.sensitive && account?.configuredFields?.includes(field.key)}<small>Configured · replace</small>{/if}
                {#if field.helpText}<small>{field.helpText}</small>{/if}
              </label>
            {/each}
          </div>
          {#if !account}
            {#if administrator && scope === "Personal" && users.length > 0}
              <label class="field"><span>Account owner</span><SelectField bind:value={ownerUserId} label="Account owner" options={users.map((user) => ({ value: user.id, label: user.displayName + (user.id === currentUserId ? " (you)" : "") }))} /></label>
              {#if ownerUserId && ownerUserId !== currentUserId}<p class="credential-safety">Only that user's listening will use this account.</p>{/if}
            {/if}
            <details class="connection-name"><summary>Give this account a name</summary><label class="field"><span>Connection name</span><input name="displayName" placeholder={selected.name} /></label></details>
            <label class="field">
              <span>Who can use it?</span>
              <SelectField name="scope" label="Who can use it?" bind:value={scope} options={[
                ...supportedAudiences(selected).filter((value) => administrator || value === "Personal")
                  .map((value) => ({ value, label: value === "Shared" ? "Everyone in the house" : "Just me" })),
              ]} />
              <small>{scope === "Shared" ? sharingDescription(selected) : administrator ? "Only the selected owner can use this provider connection." : "Only you can use this provider connection."}</small>
            </label>
            {#if scope === "Shared"}
              <label class="toggle-line"><Checkbox name="confirmShare" required /><span>I agree to share provider access with household users.<small>Other users may consume this account’s provider limits. Credentials stay hidden. Supported capabilities may include playlists and scrobbling.</small></span></label>
            {/if}
          {/if}
          <p class="connection-check-note">{testConnection ? "The saved connection is checked after you connect." : "You can check the connection after saving."}{#if selected.id === "lastfm"} Your Last.fm password is used once to get a sign-in token.{/if}</p>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer class="dialog-actions">
            {#if !account}<Button variant="text" type="button" disabled={saving} onclick={() => step = 1}>Back</Button>{/if}
            <Dialog.Close type="button" class={buttonVariants({ variant: "text" })}>Cancel</Dialog.Close>
            <Button type="submit" disabled={saving}>{saving ? "Saving…" : account ? "Save changes" : "Connect"}</Button>
          </footer>
        </form>
      {:else}
        <div class="compact-empty"><strong>No account-based Sources available</strong><p>Install or enable a provider that declares account settings.</p></div>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  .connection-steps { display: flex; gap: var(--space-5); margin: 0; padding: 0; list-style: none; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .connection-steps li { display: flex; align-items: center; gap: var(--space-2); }
  .connection-steps li > span { display: grid; place-items: center; width: 22px; height: 22px; border-radius: 50%; background: var(--color-panel-raised); }
  .connection-steps [aria-current="step"] > span { background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  .connection-steps [aria-current="step"] { color: var(--color-signal-text); font-weight: 600; }
  .connection-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: var(--space-3); padding-block: var(--space-2); }
  .connection-tile { display: grid; min-height: 112px; min-width: 0; gap: var(--space-2); align-content: start; border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-panel); color: var(--color-ink); text-align: left; cursor: pointer; }
  .connection-tile[aria-checked="true"] { outline: 1px solid var(--color-signal); border-color: var(--color-signal); }
  :global(.connect-source-dialog) { --dialog-width: 44rem; padding: var(--space-5) var(--space-5) 0; max-width: min(44rem, calc(100vw - 2rem)); }
  :global(.connect-source-dialog) > header { border: 0; padding: 0; margin-bottom: var(--space-3); }
  :global(.connect-source-dialog) > form { padding: 0; }
  .connection-steps + header { margin-top: var(--space-4); }
  /* The footer carries the dialog's bottom padding, so scrolled tiles never show beneath it. */
  .dialog-actions { position: sticky; bottom: 0; z-index: 1; margin: var(--space-3) 0 0; padding: var(--space-3) 0 var(--space-5); border-top: 1px solid var(--color-edge); background: var(--color-panel); }
  .connection-tile small, .connection-check-note { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .connection-tile strong { font-size: var(--text-sm); }
  .connection-check-note { margin: 0; }
  .connection-name summary { min-height: 44px; display: flex; align-items: center; color: var(--color-ink-muted); font-size: var(--text-sm); cursor: pointer; }
  @media (max-width: 599px) { .connection-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }

  .account-input {
    min-width: 0;
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-sm);
    background: var(--color-panel-raised);
    color: var(--color-ink);
    padding: 0 0.9rem;
    font-weight: 400;
  }

  .account-input:hover:not(:disabled) {
    border-color: var(--color-ink-muted);
  }

  .account-input:focus-visible {
    border-color: var(--color-signal);
    outline: 1px solid var(--color-signal);
    outline-offset: 0;
  }

  textarea.account-input {
    width: 100%;
    min-height: 9rem;
    max-height: 18rem;
    resize: vertical;
    padding: 0.75rem 0.9rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 0.875rem;
    line-height: 1.4;
  }
</style>
