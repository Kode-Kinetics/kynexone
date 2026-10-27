'use client';

/** A switch (role="switch"): the label is the caller's, bound by `id` with <label htmlFor> or aria-label. */
export function Toggle({ id, checked, onChange, ariaLabel }: { id: string; checked: boolean; onChange: (v: boolean) => void; ariaLabel?: string }) {
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked ? 'true' : 'false'}
      aria-label={ariaLabel}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-focus focus-visible:ring-offset-2 focus-visible:ring-offset-surface ${
        checked ? 'bg-primary' : 'bg-line-strong'
      }`}
    >
      <span className={`inline-block h-4 w-4 transform rounded-full bg-white shadow-e1 transition-transform ${
        // Knob travel is a transform, which does not mirror on its own — a switch that
        // slides off the track in Arabic is worse than one that does not mirror at all.
        checked ? 'translate-x-6 rtl:-translate-x-6' : 'translate-x-1 rtl:-translate-x-1'
      }`} />
    </button>
  );
}
