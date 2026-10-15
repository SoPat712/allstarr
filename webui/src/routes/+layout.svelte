<script lang="ts">
  import "../app.css";
  import Toaster from "$lib/components/Toaster.svelte";

  let { children } = $props();
</script>

{@render children()}

<Toaster />
