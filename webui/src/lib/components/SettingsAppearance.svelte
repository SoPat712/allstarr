<script lang="ts">
  import { onMount } from "svelte";
  import { readThemeMode, saveThemeMode, onThemeModeChange, type ThemeMode } from "$lib/theme";
  import { toast } from "$lib/toasts";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  let mode = $state<ThemeMode>("system");
  function choose(next: string) {
    const previous = mode; mode = next as ThemeMode; saveThemeMode(mode);
    toast.success("Saved · Theme", { label: "Undo", run: () => { mode = previous; saveThemeMode(previous); } });
  }
  onMount(() => { mode = readThemeMode(); return onThemeModeChange(next => mode = next); });
</script>
<PageStatus items={[{ label: "Saved on this device" }]} />
<section class="settings-card"><header><h3>Theme</h3></header><div class="settings-row"><div><strong>Colors</strong><small>Follows your device unless you pick one.</small></div><ThreeDetentSwitch value={mode} name="theme" label="Color theme" options={[{ value: "system", label: "Device" }, { value: "light", label: "Light" }, { value: "dark", label: "Dark" }]} onchange={choose} /></div></section>
