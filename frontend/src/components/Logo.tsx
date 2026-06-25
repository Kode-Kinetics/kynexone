'use client';

import { useId } from 'react';

interface LogoProps {
  /** Render only the mark (no wordmark text). */
  collapsed?: boolean;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  theme?: 'light' | 'dark';
}

/**
 * KynexOne mark — a clean rounded-square monogram: a crisp "K" with a single
 * accent node, a subtle sapphire→indigo gradient and one soft top highlight.
 * Deliberately flat-with-a-hint-of-depth so it stays sharp at 24–28px.
 */
export function Logo({ collapsed, size = 'md', theme = 'light' }: LogoProps) {
  const dim = size === 'xl' ? 56 : size === 'lg' ? 44 : size === 'sm' ? 30 : 38;
  const inverse = theme === 'dark';
  const uid = useId().replace(/:/g, '');
  const id = (n: string) => `${n}-${uid}`;

  return (
    <div className="flex items-center gap-2.5">
      <svg
        width={dim}
        height={dim}
        viewBox="0 0 40 40"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        role="img"
        aria-label="KynexOne"
        className="kx-logo shrink-0"
      >
        <defs>
          <linearGradient id={id('bg')} x1="6" y1="4" x2="34" y2="36" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#3B82F6" />
            <stop offset="100%" stopColor="#1E3A8A" />
          </linearGradient>
          <linearGradient id={id('hi')} x1="6" y1="6" x2="20" y2="20" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#ffffff" stopOpacity="0.30" />
            <stop offset="100%" stopColor="#ffffff" stopOpacity="0" />
          </linearGradient>
          <filter id={id('shadow')} x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="0" dy="2" stdDeviation="2.2" floodColor="#07111f" floodOpacity="0.24" />
            <feDropShadow dx="0" dy="8" stdDeviation="10" floodColor="#2563eb" floodOpacity="0.18" />
          </filter>
        </defs>

        <g filter={`url(#${id('shadow')})`}>
          <rect x="4" y="4" width="32" height="32" rx="9" fill={`url(#${id('bg')})`} />
          <rect x="4" y="4" width="32" height="32" rx="9" fill={`url(#${id('hi')})`} />
          <rect x="5.25" y="5.75" width="30" height="29.5" rx="8.5" fill="#0f172a" opacity="0.10" />
          <path
            d="M15.5 12 V28 M15.5 20.2 L25 12 M16.4 19.6 L25.4 28"
            stroke="#ffffff"
            strokeWidth="2.6"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
          <circle cx="28.4" cy="11.6" r="2.5" fill="#22D3EE" />
          <rect x="4.5" y="4.5" width="31" height="31" rx="8.5" fill="none" stroke="#ffffff" strokeOpacity={inverse ? 0.16 : 0.1} strokeWidth="1" />
        </g>

        <style>{`
          .kx-logo { transition: transform .25s cubic-bezier(.2,.8,.2,1); }
          .kx-logo:hover { transform: translateY(-1px) scale(1.04); }
          @media (prefers-reduced-motion: reduce) { .kx-logo, .kx-logo:hover { transition: none; transform: none; } }
        `}</style>
      </svg>

      {!collapsed && (
        <div className="min-w-0 leading-tight">
          <p className={`truncate text-[15px] font-bold tracking-tight ${inverse ? 'text-white' : 'text-slate-900 dark:text-white'}`}>
            Kynex<span className={inverse ? 'text-cyan-300' : 'text-blue-600 dark:text-cyan-300'}>One</span>
          </p>
          <p className={`truncate text-[10px] font-medium tracking-wide ${inverse ? 'text-white/55' : 'text-slate-400'}`}>
            Workforce Platform
          </p>
        </div>
      )}
    </div>
  );
}
