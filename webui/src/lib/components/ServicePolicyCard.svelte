<script lang="ts">
  import { settings, type ConfigField } from "$lib/api";
  import { fieldValue } from "$lib/settings";
  import { waitForRestart } from "$lib/restart";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  let { fields, config }: { fields: ConfigField[]; config: Record<string, unknown> } = $props();
  const help: Record<string, string> = {
    ODESLI_ENABLED: "Finds cross-service song links with Allstarr's own lookups using Deezer, Apple Music and enabled sources. Linked tracks are checked against your metadata sources. Song.link Web, UniTune, MusicBrainz and Squigly provide fallback links without a key.",
    ODESLI_RATE_LIMIT_MS: "Wait between official song.link API lookups when a key is configured. Other link services use their own request limits.",
    ODESLI_API_KEY: "Optional song.link API key. Leave unchanged to keep it, or clear to remove it. With a key, the official API is tried first, followed by the fallback link services.",
    EXTENSIONS_ALLOW_REMOTE_INSTALL: "Off: extensions can be installed and updated only from your trusted admin networks, such as your home network. On: also from anywhere that can reach this admin page.",
  };
  // Plain names for the settings; "(ms)" and "Optional" belong in the help text, not the label.
  const names: Record<string, string> = {
    ODESLI_ENABLED: "Cross-service song links",
    ODESLI_RATE_LIMIT_MS: "Wait between song.link lookups (milliseconds)",
    ODESLI_API_KEY: "song.link API key",
    EXTENSIONS_ALLOW_REMOTE_INSTALL: "Install extensions from outside your home network",
  };
  const label = (field: ConfigField) => names[field.key] ?? field.label;
  let changedSecrets = $state<Record<string, boolean>>({});
  let busy = $state(false), error = $state(""), saved = $state(false);
  async function save(event: SubmitEvent) {
    event.preventDefault(); busy = true; error = "";
    const data = new FormData(event.currentTarget as HTMLFormElement);
    try {
      const result = await settings.save(Object.fromEntries(fields.filter((field) => !field.sensitive || changedSecrets[field.key]).map((field) => [field.key, field.type === "toggle" ? String(data.get(field.key) === "true") : String(data.get(field.key) ?? "")])));
      saved = true;
      if (result.requiresRestart) { await waitForRestart(); window.location.reload(); }
    } catch (cause) { error = cause instanceof Error ? cause.message : "Service policy could not be saved."; }
    finally { busy = false; }
  }
</script>
{#if fields.length}
  <form class="settings-card settings-disclosure" aria-label="Song links and extension installs" onsubmit={(event) => void save(event)}>
    <header><h3>Song links and extension installs</h3></header>
    <div class="settings-fields">
      {#each fields as field}
        <label class="setting-field settings-row">
          <div><strong>{label(field)}</strong>{#if help[field.key]}<small>{help[field.key]}</small>{/if}</div>
          {#if field.type === "toggle"}
            <span class="settings-toggle"><Switch name={field.key} checked={String(fieldValue(config, field) ?? false) === "true"} value="true" disabled={busy} aria-label={field.label} /></span>
          {:else}
            <input name={field.key} value={String(fieldValue(config, field) ?? "")} type={field.sensitive ? "password" : "number"} oninput={() => { if (field.sensitive) changedSecrets[field.key] = true; }} autocomplete={field.sensitive ? "new-password" : undefined} disabled={busy} />
          {/if}
        </label>
      {/each}
      <footer>
        {#if saved}<span role="status">Saved.</span>{:else}<span>Saving restarts Allstarr.</span>{/if}
        <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save and restart"}</Button>
      </footer>
    </div>
  </form>
{/if}
{#if error}<p class="notice-error m-4" role="alert">{error}</p>{/if}
