'use client';

interface AvatarProps {
  name: string;
  size?: 'xs' | 'sm' | 'md' | 'lg';
}

const sizeClasses = {
  xs: 'h-6 w-6 text-[10px]',
  sm: 'h-7 w-7 text-xs',
  md: 'h-8 w-8 text-xs',
  lg: 'h-10 w-10 text-sm',
};

// Every swatch keeps its initials at ≥4.5:1 (WCAG AA) — amber-500/rose-500 with white did not.
const palette = [
  'bg-blue-600 text-white',
  'bg-emeraldZ text-midnight',
  'bg-violet-600 text-white',
  'bg-amber-700 text-white',
  'bg-rose-600 text-white',
  'bg-cyan-700 text-white',
];

function initials(name: string) {
  const parts = name.trim().split(' ').filter(Boolean);
  if (parts.length === 1) return parts[0][0].toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

function colorFor(name: string) {
  const n = name.split('').reduce((a, c) => a + c.charCodeAt(0), 0);
  return palette[n % palette.length];
}

export function Avatar({ name, size = 'md' }: AvatarProps) {
  return (
    <span
      className={`grid shrink-0 place-items-center rounded-full font-bold ${sizeClasses[size]} ${colorFor(name)}`}
      aria-hidden="true"
    >
      {initials(name)}
    </span>
  );
}
