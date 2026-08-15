<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    children,
    tone,
    state = "",
    class: className = "",
  }: {
    children: Snippet;
    tone?: "neutral" | "accent" | "success" | "warning" | "danger";
    state?: string;
    class?: string;
  } = $props();

  const success = new Set(["accepted", "pinned", "healthy", "configured", "ready", "enabled", "completed", "succeeded", "delivered", "resolved"]);
  const danger = new Set(["unresolved", "ambiguous", "rejected", "degraded", "failed", "error", "blocked", "cancelled", "unavailable", "unauthorized"]);
  const accent = new Set(["available", "active", "pending", "running", "testing", "refreshing"]);

  const resolvedTone = $derived(tone ?? (
    success.has(state.toLowerCase()) ? "success" :
    danger.has(state.toLowerCase()) ? "danger" :
    accent.has(state.toLowerCase()) ? "accent" :
    state ? "warning" : "neutral"
  ));
</script>

<span data-slot="badge" class={`badge badge-${resolvedTone} ${className}`.trim()}>{@render children()}</span>
