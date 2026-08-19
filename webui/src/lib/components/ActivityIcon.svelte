<script lang="ts">
  import {
    Activity,
    ArrowDownToLine,
    AudioLines,
    BookOpen,
    Boxes,
    CircleGauge,
    HeartPulse,
    ListMusic,
    Radio,
    RefreshCw,
    Settings,
  } from "@lucide/svelte";

  let { kind, size = 18 }: { kind: string; size?: number } = $props();
  const icons = {
    administration: Settings,
    caching: ArrowDownToLine,
    extension: Boxes,
    job: RefreshCw,
    library: BookOpen,
    matching: CircleGauge,
    playlist: ListMusic,
    provider_health: HeartPulse,
    scrobble: Radio,
    streaming: AudioLines,
  } as const;
  const Icon = $derived(icons[kind as keyof typeof icons] ?? Activity);
</script>

<Icon {size} strokeWidth={1.8} aria-hidden="true" />
