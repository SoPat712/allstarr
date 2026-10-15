<script lang="ts">
	import { Progress as ProgressPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		max = 100,
		value,
		...restProps
	}: WithoutChildrenOrChild<ProgressPrimitive.RootProps> = $props();
</script>

<ProgressPrimitive.Root
	bind:ref
	data-slot="progress"
	class={cn("h-1 rounded-full bg-muted relative flex w-full items-center overflow-x-hidden", className)}
	{value}
	{max}
	{...restProps}
>
	<div
		data-slot="progress-indicator"
		class="bg-primary h-full transition-[width] motion-reduce:transition-none"
		style="width: {Math.min(100, Math.max(0, (100 * (value ?? 0)) / ((max ?? 100) > 0 ? (max ?? 100) : 100)))}%"
	></div>
</ProgressPrimitive.Root>
