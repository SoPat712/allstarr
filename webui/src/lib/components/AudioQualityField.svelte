<script lang="ts">
  const steps = [
    {
      value: "DataSaver",
      label: "Data saver",
      summary: "About 96–128 kbps where the source offers it. Uses the least data.",
      providers: ["Apple Music: AAC 96 kbps", "Deezer: MP3 128 kbps", "Qobuz: MP3 320 kbps (its lowest option)"],
    },
    {
      value: "High",
      label: "High lossy",
      summary: "About 256–320 kbps. Smaller files and streams with high sound quality.",
      providers: ["Apple Music: AAC 320 kbps", "Deezer: MP3 320 kbps", "Qobuz: MP3 320 kbps"],
    },
    {
      value: "CdLossless",
      label: "CD lossless",
      summary: "Up to 16-bit/44.1 kHz. Keeps CD-quality audio without lossy compression.",
      providers: ["Apple Music: ALAC 16-bit/44.1 kHz", "Deezer: FLAC when available", "Qobuz: FLAC 16-bit/44.1 kHz"],
    },
    {
      value: "HiResLossless",
      label: "Hi-Res lossless",
      summary: "Up to 24-bit/96 kHz when the source provides it. Uses more storage and bandwidth.",
      providers: ["Apple Music: ALAC up to 24-bit/96 kHz", "Deezer: FLAC (its nearest lower option)", "Qobuz: FLAC up to 24-bit/96 kHz when allowed"],
    },
    {
      value: "BestAvailable",
      label: "Best available",
      summary: "Uses the best quality each source provides. Recommended when data use is not a concern.",
      providers: ["Apple Music: up to ALAC 24-bit/192 kHz", "Deezer: FLAC", "Qobuz: the best quality available for the song"],
    },
  ] as const;

  let { value, name, onchange }: { value: string; name: string; onchange?: () => void } = $props();
  let selected = $state(steps.length - 1);
  let lastValue = $state("");
  const current = $derived(steps[selected] ?? steps.at(-1)!);

  $effect.pre(() => {
    if (value === lastValue) return;
    selected = Math.max(0, steps.findIndex((step) => step.value === value));
    lastValue = value;
  });
</script>

<div class="audio-quality-control">
  <input
    type="range"
    min="0"
    max={steps.length - 1}
    step="1"
    value={selected}
    aria-label="Audio quality"
    aria-valuetext={`${current.label}. ${current.summary}`}
    oninput={(event) => {
      selected = Number(event.currentTarget.value);
      onchange?.();
    }}
  />
  <input type="hidden" {name} value={current.value} />
  <div class="audio-quality-steps" aria-hidden="true">
    {#each steps as step}<span class:active={step.value === current.value}>{step.label}</span>{/each}
  </div>
  <p aria-live="polite"><strong>{current.label}</strong><span>{current.summary}</span></p>
  <details>
    <summary>What each music source will use</summary>
    <ul>{#each current.providers as provider}<li>{provider}</li>{/each}</ul>
  </details>
</div>
