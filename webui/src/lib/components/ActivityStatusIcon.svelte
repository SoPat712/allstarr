<script lang="ts">
  import { ArrowDown, Check, CircleAlert, Minus, Pause, RotateCw } from "@lucide/svelte";
  import { activityTone, humanize } from "$lib/activity";
  let { state, severity = "info", kind = "", label }: { state: string; severity?: string; kind?: string; label?: string } = $props();
  const tone = $derived(activityTone(state, severity));
  const Icon = $derived(["caching", "download"].includes(kind) && tone === "success" ? ArrowDown : tone === "success" ? Check : ["danger", "warning"].includes(tone) ? CircleAlert : ["waiting", "deferred"].includes(state.toLowerCase()) ? Pause : tone === "accent" ? RotateCw : Minus);
</script>
<span class="activity-status-icon" data-tone={tone} data-kind={kind} role="img" aria-label={label ?? humanize(state)}><Icon size={15} aria-hidden="true" /></span>
<style>
  .activity-status-icon { display: grid; width: 28px; height: 28px; place-items: center; flex: none; border-radius: 50%; background: var(--color-panel-raised); color: var(--color-ink-muted); }
  [data-tone="success"] { background: var(--success-container); color: var(--on-success-container); }
  [data-tone="accent"], [data-tone="success"]:is([data-kind="caching"], [data-kind="download"]) { background: var(--md-sys-color-primary-container); color: var(--md-sys-color-on-primary-container); }
  [data-tone="warning"] { background: var(--warning-container); color: var(--on-warning-container); }
  [data-tone="danger"] { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); }
</style>
