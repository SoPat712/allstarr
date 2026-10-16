<script lang="ts">
  import { settings, type ConfigField } from "$lib/api";
  import { fieldValue } from "$lib/settings";
  import { waitForRestart } from "$lib/restart";
  import { Button } from "$lib/components/ui/button";
  let { fields, config }: { fields: ConfigField[]; config: Record<string, unknown> } = $props();
  let busy = $state(false), error = $state(""), saved = $state(false);
  async function save(event: SubmitEvent) {
    event.preventDefault(); busy = true; error = "";
    const data = new FormData(event.currentTarget as HTMLFormElement);
    try {
      const result = await settings.save(Object.fromEntries(fields.map((field) => [field.key, String(data.get(field.key) ?? "")])));
      saved = true;
      if (result.requiresRestart) { await waitForRestart(); window.location.reload(); }
    } catch (cause) { error = cause instanceof Error ? cause.message : "Service policy could not be saved."; }
    finally { busy = false; }
  }
</script>
{#if fields.length}
  <form class="settings-card p-5 m-4 space-y-4" aria-label="Service policy" onsubmit={(event) => void save(event)}>
    <h3>Service and extension policy</h3><p>Control optional link lookups and installing remote extension packages. Changes restart Allstarr.</p>
    {#each fields as field}
      <label class="block">{field.label}
        {#if field.type === "toggle"}<select class="w-full mt-1" name={field.key} value={String(fieldValue(config, field) ?? false)} disabled={busy}><option value="false">Off</option><option value="true">On</option></select>
        {:else}<input class="w-full mt-1" name={field.key} value={String(fieldValue(config, field) ?? "")} type="number" min={field.min ?? undefined} max={field.max ?? undefined} disabled={busy} />{/if}
      </label>
    {/each}
    {#if saved}<p role="status">Service policy saved.</p>{/if}
    <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save service policy"}</Button>
  </form>
{/if}
{#if error}<p class="notice-error m-4" role="alert">{error}</p>{/if}
