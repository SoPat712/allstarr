<script lang="ts">
  import {
    Activity,
    Check,
    Headphones,
    Flag,
    House,
    Library,
    LogOut,
    MoreHorizontal,
    Settings,
    Unlink,
    Waypoints,
  } from "@lucide/svelte";

  let { name, size = 18 }: { name: string; size?: number } = $props();
  const icons = {
    activity: Activity,
    check: Check,
    headphones: Headphones,
    home: House,
    library: Library,
    logout: LogOut,
    more: MoreHorizontal,
    report: Flag,
    settings: Settings,
    sources: Waypoints,
    unlink: Unlink,
  };
  const Icon = $derived(icons[name as keyof typeof icons] ?? House);
</script>

<Icon class="ui-icon" {size} strokeWidth={1.8} aria-hidden="true" />
