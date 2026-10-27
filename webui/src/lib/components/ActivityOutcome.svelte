<script lang="ts">
  import { Check, CircleAlert, CircleMinus, LoaderCircle, TriangleAlert } from "@lucide/svelte";
  import { activityTone, humanize } from "$lib/activity";
  import { Badge } from "$lib/components/ui/badge";

  let { state, severity = "info", label }: { state: string; severity?: string; label?: string } = $props();
  const tone = $derived(activityTone(state, severity));
  const icons = { success: Check, danger: CircleAlert, accent: LoaderCircle, warning: TriangleAlert, neutral: CircleMinus };
  const Icon = $derived(icons[tone]);
</script>

<Badge {tone} class="activity-outcome"><Icon size={14} aria-hidden="true" />{label ?? humanize(state)}</Badge>
