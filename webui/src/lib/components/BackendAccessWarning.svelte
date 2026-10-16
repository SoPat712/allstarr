<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { onMount } from "svelte";
  import { setupAccess } from "$lib/api";
  let missing = $state(false);
  onMount(() => {
    let active = true;
    void setupAccess.backend().then(({ settings }) => {
      if (active) missing = settings.supported && (!settings.configured || settings.needsReview);
    }).catch(() => {});
    return () => { active = false; };
  });
</script>
{#if missing}
  <div class="degraded-banner" role="status">
    <span aria-hidden="true">!</span>
    <p><strong>Media-server access needs attention.</strong> Restore access to resume playlist sync and library scans.</p>
    <Button variant="secondary" href="#/settings/access">Open media-server access</Button>
  </div>
{/if}
