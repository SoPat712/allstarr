<script lang="ts">
  import { settings, type ConfigField } from "$lib/api";
  import { fieldValue } from "$lib/settings";
  import { waitForRestart } from "$lib/restart";
  import { Button } from "$lib/components/ui/button";
  let { fields, config }: { fields: ConfigField[]; config: Record<string, unknown> } = $props();
  const help: Record<string, string> = {
    ODESLI_ENABLED: "Finds cross-service song links with Allstarr's own lookups using Deezer, Apple Music and enabled sources. Only sources in your metadata order are used; song.link is optional with an API key.",
    ODESLI_RATE_LIMIT_MS: "Wait between optional song.link lookups when an API key is configured.",
    ODESLI_API_KEY: "Optional song.link API key. Leave unchanged to keep it, or clear to remove it. Song links are sent to song.link only when a key is configured.",
    EXTENSIONS_ALLOW_REMOTE_INSTALL: "Off: extensions can be installed and updated only from your trusted admin networks, such as your home network. On: also from anywhere that can reach this admin page.",
  };
  let changedSecrets = $state<Record<string, boolean>>({});
  let busy = $state(false), error = $state(""), saved = $state(false);
  async function save(event: SubmitEvent) {
    event.preventDefault(); busy = true; error = "";
    const data = new FormData(event.currentTarget as HTMLFormElement);
    try {
      const result = await settings.save(Object.fromEntries(fields.filter((field) => !field.sensitive || changedSecrets[field.key]).map((field) => [field.key, String(data.get(field.key) ?? "")])));
      saved = true;
      if (result.requiresRestart) { await waitForRestart(); window.location.reload(); }
    } catch (cause) { error = cause instanceof Error ? cause.message : "Service policy could not be saved."; }
    finally { busy = false; }
  }
</script>
{#if fields.length}
  <form class="settings-card p-5 my-4 space-y-4" aria-label="Song links and extension installs" onsubmit={(event) => void save(event)}>
    <h3 class="font-semibold">Song links and extension installs</h3><p>Changes restart Allstarr.</p>
    {#each fields as field}
      <label class="field"><span>{field.label}</span>
        {#if field.type === "toggle"}<select name={field.key} value={String(fieldValue(config, field) ?? false)} disabled={busy}><option value="false">Off</option><option value="true">On</option></select>
        {:else}<input name={field.key} value={String(fieldValue(config, field) ?? "")} type={field.sensitive ? "password" : "number"} oninput={() => { if (field.sensitive) changedSecrets[field.key] = true; }} autocomplete={field.sensitive ? "new-password" : undefined} min={field.min ?? undefined} max={field.max ?? undefined} disabled={busy} />{/if}
        {#if help[field.key]}<small>{help[field.key]}</small>{/if}
      </label>
    {/each}
    {#if saved}<p role="status">Service policy saved.</p>{/if}
    <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save and restart"}</Button>
  </form>
{/if}
{#if error}<p class="notice-error m-4" role="alert">{error}</p>{/if}
