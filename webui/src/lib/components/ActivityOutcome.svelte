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

<style>
  :global(.activity-outcome) { gap: var(--space-1); border-color: transparent; color: var(--activity-ink, var(--md-sys-color-on-surface-variant)); background: var(--activity-container, var(--md-sys-color-surface-container-highest)); line-height: 1.25; }
  :global(.activity-outcome.badge-success) { --activity-ink: var(--on-success-container); --activity-container: var(--success-container); }
  :global(.activity-outcome.badge-accent) { --activity-ink: var(--md-sys-color-on-primary-container); --activity-container: var(--md-sys-color-primary-container); }
  :global(.activity-outcome.badge-warning) { --activity-ink: var(--on-warning-container); --activity-container: var(--warning-container); }
  :global(.activity-outcome.badge-danger) { --activity-ink: var(--md-sys-color-on-error-container); --activity-container: var(--md-sys-color-error-container); }
</style>
