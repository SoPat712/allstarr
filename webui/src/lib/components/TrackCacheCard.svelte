<script lang="ts">
  import { onMount } from "svelte";
  import { settings } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { pathValue } from "$lib/settings";
  import { Button } from "$lib/components/ui/button";
  import SelectField from "$lib/components/SelectField.svelte";

  let storageMode = $state("Cache");
  let cacheDurationHours = $state(72);
  let transcodeCacheMinutes = $state(60);
  let ready = $state(false);
  let loadError = $state("");
  let dirty = $state(false);
  let saving = $state(false);

  async function load() {
    try {
      const config = await settings.config();
      storageMode = String(pathValue(config, "library.storageMode") ?? "Cache");
      cacheDurationHours = Number(pathValue(config, "library.cacheDurationHours") ?? 72);
      transcodeCacheMinutes = Number(pathValue(config, "cache.transcodeCacheMinutes") ?? 60);
      ready = true;
      loadError = "";
    } catch (cause) {
      loadError = cause instanceof Error ? cause.message : "Track cache settings are unavailable.";
    }
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (saving) return;
    saving = true;
    try {
      await settings.save({
        STORAGE_MODE: storageMode,
        CACHE_DURATION_HOURS: String(cacheDurationHours),
        CACHE_TRANSCODE_MINUTES: String(transcodeCacheMinutes),
      });
      dirty = false;
      toast.success("Track cache settings saved.");
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Track cache settings could not be saved.");
    } finally {
      saving = false;
    }
  }

  onMount(() => void load());
</script>

<section class="settings-card" aria-labelledby="track-cache-title">
  <header><div><h3 id="track-cache-title">Track cache</h3><small>Songs saved after streaming so they start faster next time. Kept songs are never removed.</small></div></header>
  {#if ready}
    <form class="settings-fields track-cache-settings" oninput={() => { dirty = true; }} onsubmit={save}>
      <label class="setting-field settings-row">
        <div><strong>Playback caching</strong><small>Hearts, Keep and retained playlists protect their copies in either mode.</small></div>
        <SelectField bind:value={storageMode} name="STORAGE_MODE" label="Playback caching" options={[{ value: "Cache", label: "Cache played songs" }, { value: "Permanent", label: "Do not cache playback" }]} onchange={() => { dirty = true; }} />
      </label>
      <label class="setting-field settings-row">
        <div><strong>Track retention (hours)</strong><small>Hours since last use. The size limit can evict cached tracks sooner.</small></div>
        <input bind:value={cacheDurationHours} name="CACHE_DURATION_HOURS" type="number" min="1" max="8760" />
      </label>
      <label class="setting-field settings-row">
        <div><strong>Quality override retention (minutes)</strong><small>Temporary lower-bandwidth versions use this shorter window.</small></div>
        <input bind:value={transcodeCacheMinutes} name="CACHE_TRANSCODE_MINUTES" type="number" min="1" max="10080" />
      </label>
      <footer>
        {#if dirty}<span>Unsaved changes</span>{/if}
        <Button type="submit" disabled={saving || !dirty}>{saving ? "Saving…" : "Save track cache"}</Button>
      </footer>
    </form>
  {:else}
    <p class="inline-status" role="status">{loadError || "Loading track cache settings…"}</p>
  {/if}
</section>
