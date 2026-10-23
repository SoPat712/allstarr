<script lang="ts">
  import type { ProviderUseMode } from "$lib/api";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";

  let { value, label, disabled = false, onchange }: {
    value: ProviderUseMode; label: string; disabled?: boolean; onchange: (mode: ProviderUseMode) => void;
  } = $props();

  const options = [
    { value: "automatic", label: "On", tone: "primary" },
    { value: "manualOnly", label: "Manual only" },
    { value: "off", label: "Off", tone: "neutral" },
  ] as const;
</script>

<ThreeDetentSwitch {value} {label} {options} {disabled} {onchange} widths={[27.5, 45, 27.5]} />
