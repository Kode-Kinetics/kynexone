'use client';

/**
 * A reconciliation number that opens the employees behind it. A figure with nothing behind it (zero)
 * is plain text: a button that opens an empty list helps nobody. The number sits in <bdi> so its digits
 * and sign keep their order inside right-to-left text.
 */
export function ReconFigure({ children, label, onOpen, plain = false }: {
  children: React.ReactNode;
  /** What the button does, in words: includes the figure so the visible text is part of the name. */
  label: string;
  onOpen: (opener: HTMLElement) => void;
  plain?: boolean;
}) {
  if (plain) return <bdi>{children}</bdi>;
  return (
    <button
      type="button"
      aria-label={label}
      onClick={e => onOpen(e.currentTarget)}
      className="rounded px-1 font-medium tabular-nums text-fg underline decoration-dotted underline-offset-4 hover:bg-primary-soft focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      <bdi>{children}</bdi>
    </button>
  );
}
