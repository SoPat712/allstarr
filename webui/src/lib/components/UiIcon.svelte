<script lang="ts">
  import {
    Activity,
    Headphones,
    House,
    Library,
    LogOut,
    Settings,
    Waypoints,
  } from "@lucide/svelte";

  let { name, size = 18 }: { name: string; size?: number } = $props();
  const icons = {
    activity: Activity,
    headphones: Headphones,
    home: House,
    library: Library,
    logout: LogOut,
    settings: Settings,
    sources: Waypoints,
  };
  const Icon = $derived(icons[name as keyof typeof icons] ?? House);
</script>

<Icon class="ui-icon" {size} strokeWidth={1.8} aria-hidden="true" />
