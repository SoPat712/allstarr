<script lang="ts">
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
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

  let { value, name, onchange, showDetails = true, requestedQualities = $bindable<readonly string[]>([]) }: { value: string; name: string; onchange?: () => void; showDetails?: boolean; requestedQualities?: readonly string[] } = $props();
  let selected = $state(steps.length - 1);
  let lastValue = $state("");
  const current = $derived(steps[selected] ?? steps.at(-1)!);
  $effect(() => { requestedQualities = current.providers; });
  const pick = (index: number) => { selected = index; onchange?.(); };

  $effect.pre(() => {
    if (value === lastValue) return;
    selected = Math.max(0, steps.findIndex((step) => step.value === value));
    lastValue = value;
  });
</script>

<div class="audio-quality-control">
  <div class="quality-slider" style={`--position:${selected / (steps.length - 1)}`}>
    <input
      type="range"
      min="0"
      max={steps.length - 1}
      step="1"
      value={selected}
      aria-label="Audio quality"
      aria-valuetext={`${current.label}. ${current.summary}`}
      oninput={(event) => pick(Number(event.currentTarget.value))}
    />
    <div class="quality-slider-track" aria-hidden="true">
      <span class="quality-slider-active"></span>
      <span class="quality-slider-inactive"></span>
      {#each steps as step, index (step.value)}
        {#if index !== selected}<span class="quality-slider-stop" class:on={index < selected} style={`--at:${index / (steps.length - 1)}`}></span>{/if}
      {/each}
      <span class="quality-slider-handle"></span>
    </div>
  </div>
  <input type="hidden" {name} value={current.value} />
  <div class="audio-quality-steps" aria-hidden="true">
    {#each steps as step, index}<button type="button" tabindex="-1" class:active={step.value === current.value} onclick={() => pick(index)}>{step.label}</button>{/each}
  </div>
  <p aria-live="polite"><strong>{current.label}</strong><span>{current.summary}</span></p>
  {#if showDetails}<details>
    <summary class="disclosure-summary compact"><DisclosureLabel title="Music source quality details" description="See the exact quality requested from each provider" /></summary>
    <ul>{#each current.providers as provider}<li>{provider}</li>{/each}</ul>
  </details>{/if}
</div>

<style>
  /* Each stop sits under its label: the labels share five columns, so the track runs from the first centre to the last. */
  .quality-slider { --edge: 0.5rem; --gap: 6px; --at-handle: calc(var(--edge) + var(--position) * (100% - 2 * var(--edge))); position: relative; height: 2.75rem; margin-inline: calc(10% - 0.66rem); }
  .quality-slider input[type="range"] { position: absolute; z-index: 1; inset: 0; width: 100%; min-height: 0; height: 100%; margin: 0; border: 0; padding: 0; background: transparent; cursor: pointer; opacity: 0; }
  .quality-slider-track { position: absolute; inset: 0; pointer-events: none; }
  .quality-slider-active, .quality-slider-inactive { position: absolute; top: 50%; height: 1rem; translate: 0 -50%; transition: left var(--motion-standard), right var(--motion-standard), width var(--motion-standard); }
  .quality-slider-active { left: 0; width: max(0px, calc(var(--at-handle) - var(--gap))); border-radius: 999px 3px 3px 999px; background: var(--md-sys-color-primary); }
  .quality-slider-inactive { right: 0; left: calc(var(--at-handle) + var(--gap)); border-radius: 3px 999px 999px 3px; background: var(--md-sys-color-secondary-container); }
  .quality-slider-stop { position: absolute; top: 50%; left: calc(var(--edge) + var(--at) * (100% - 2 * var(--edge))); width: 4px; height: 4px; border-radius: 50%; background: var(--md-sys-color-on-secondary-container); translate: -50% -50%; }
  .quality-slider-stop.on { background: var(--md-sys-color-on-primary); }
  .quality-slider-handle { position: absolute; top: 50%; left: var(--at-handle); width: 4px; height: 2.75rem; border-radius: 999px; background: var(--md-sys-color-primary); translate: -50% -50%; transition: left var(--motion-standard), width var(--motion-standard); }
  .quality-slider input:hover + .quality-slider-track .quality-slider-handle { box-shadow: 0 0 0 6px color-mix(in srgb, var(--md-sys-color-primary) 14%, transparent); }
  .quality-slider input:active + .quality-slider-track .quality-slider-handle { width: 2px; }
  .quality-slider input:focus-visible + .quality-slider-track .quality-slider-handle { outline: 2px solid var(--focus-ring); outline-offset: 3px; }
  .audio-quality-steps button { padding: var(--space-1) 0; border: 0; border-radius: var(--radius-sm, 0.5rem); background: none; color: inherit; font: inherit; cursor: pointer; transition: color var(--motion-standard); }
  .audio-quality-steps button:hover { color: var(--color-ink); }
  .audio-quality-steps button.active { color: var(--color-ink); font-weight: 700; }
</style>
