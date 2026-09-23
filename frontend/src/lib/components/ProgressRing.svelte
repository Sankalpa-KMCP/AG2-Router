<script lang="ts">
  interface Props {
    percent: number | null;
    size?: number;
    strokeWidth?: number;
    isExhausted?: boolean;
    label?: string;
  }

  let {
    percent = 0,
    size = 64,
    strokeWidth = 6,
    isExhausted = false,
    label = ''
  }: Props = $props();

  const isUnknown = $derived(percent === null || !Number.isFinite(percent));
  const safePercent = $derived(!isUnknown && percent !== null ? percent : 0);
  const clampedPercent = $derived(Math.max(0, Math.min(100, Math.round(safePercent))));
  const radius = $derived((size - strokeWidth) / 2);
  const circumference = $derived(2 * Math.PI * radius);
  const strokeDashoffset = $derived(circumference - (clampedPercent / 100) * circumference);

  const strokeColor = $derived.by(() => {
    if (isUnknown && !isExhausted) return 'var(--ring-track)';
    if (isExhausted || clampedPercent <= 0) return 'var(--ring-danger)';
    if (clampedPercent <= 15) return 'var(--ring-warning)';
    return 'var(--ring-healthy)';
  });
</script>

<div
  class="progress-ring-container"
  role={isUnknown && !isExhausted ? 'status' : 'progressbar'}
  aria-valuenow={isUnknown && !isExhausted ? undefined : clampedPercent}
  aria-valuemin={0}
  aria-valuemax={100}
  aria-label={isUnknown && !isExhausted ? `${label || 'Model quota'}: unknown` : label ? `${label}: ${clampedPercent}% remaining` : `${clampedPercent}% remaining`}
>
  <svg
    width={size}
    height={size}
    viewBox="0 0 {size} {size}"
    class="progress-ring-svg"
  >
    <!-- Background track -->
    <circle
      cx={size / 2}
      cy={size / 2}
      r={radius}
      fill="none"
      stroke="var(--ring-track)"
      stroke-width={strokeWidth}
    />
    <!-- Value progress circle -->
    <circle
      cx={size / 2}
      cy={size / 2}
      r={radius}
      fill="none"
      stroke={strokeColor}
      stroke-width={strokeWidth}
      stroke-linecap="round"
      stroke-dasharray={circumference}
      stroke-dashoffset={strokeDashoffset}
      class="progress-ring-circle"
      transform="rotate(-90 {size / 2} {size / 2})"
    />
  </svg>
  <div class="progress-ring-content">
    <span class="progress-ring-value" style="font-size: {size >= 64 ? 14 : 11}px;">
      {isUnknown && !isExhausted ? '—' : `${clampedPercent}%`}
    </span>
  </div>
</div>

<style>
  .progress-ring-container {
    position: relative;
    display: inline-flex;
    align-items: center;
    justify-content: center;
  }

  .progress-ring-svg {
    display: block;
  }

  .progress-ring-circle {
    transition: stroke-dashoffset var(--transition-normal), stroke var(--transition-normal);
  }

  .progress-ring-content {
    position: absolute;
    display: flex;
    align-items: center;
    justify-content: center;
    pointer-events: none;
  }

  .progress-ring-value {
    font-family: var(--font-sans);
    font-weight: 700;
    color: var(--color-text-primary);
    line-height: 1;
  }
</style>
