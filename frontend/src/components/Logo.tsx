'use client';

import { useId } from 'react';

interface LogoProps {
  collapsed?: boolean;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  theme?: 'light' | 'dark';
}

/**
 * KynexOne mark — an extruded 3D monogram.
 *
 * Construction (back-to-front for real depth):
 *   1. an extruded side-face offset down-right (the "thickness")
 *   2. the glass front face with a layered mesh gradient
 *   3. a fine grain overlay + top specular highlight (material realism)
 *   4. the "K" rendered as a beveled ribbon: dark depth pass + bright
 *      front pass + a thin top-edge highlight
 *   5. a glowing accent node with its own spec dot
 *   6. a sheen that sweeps across on hover
 *
 * All gradient/filter ids are namespaced with useId() so multiple logos can
 * coexist on one page without cross-referencing each other's defs (a subtle
 * Safari bug with duplicate SVG ids).
 */
export function Logo({ collapsed, size = 'md', theme = 'light' }: LogoProps) {
  const dim = size === 'xl' ? 64 : size === 'lg' ? 48 : size === 'sm' ? 28 : 40;
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
        className={`kx-logo shrink-0 ${
          inverse
            ? 'drop-shadow-[0_22px_40px_rgba(29,78,216,0.34)]'
            : 'drop-shadow-[0_20px_34px_rgba(37,99,235,0.18)]'
        }`}
      >
        <defs>
          {/* Front-face mesh: bright corner light → deep core */}
          <linearGradient id={id('face')} x1="9" y1="5" x2="34" y2="36" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#4f8bff" />
            <stop offset="45%" stopColor="#2356d6" />
            <stop offset="100%" stopColor="#0a1c63" />
          </linearGradient>
          {/* Radial bloom of light from the top-left for a 3D sphere-ish read */}
          <radialGradient id={id('bloom')} cx="28%" cy="20%" r="85%" gradientUnits="objectBoundingBox">
            <stop offset="0%" stopColor="#bfdcff" stopOpacity="0.55" />
            <stop offset="55%" stopColor="#bfdcff" stopOpacity="0" />
          </radialGradient>
          {/* Extruded side wall */}
          <linearGradient id={id('wall')} x1="10" y1="8" x2="34" y2="36" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#0a1a55" />
            <stop offset="100%" stopColor="#040e33" />
          </linearGradient>
          {/* Glass rim light */}
          <linearGradient id={id('rim')} x1="8" y1="6" x2="34" y2="34" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#ffffff" stopOpacity="0.9" />
            <stop offset="42%" stopColor="#cfe4ff" stopOpacity="0.25" />
            <stop offset="100%" stopColor="#5aa0ff" stopOpacity="0.12" />
          </linearGradient>
          {/* K front material */}
          <linearGradient id={id('k')} x1="15" y1="11" x2="27" y2="29" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#ffffff" />
            <stop offset="100%" stopColor="#d6e6ff" />
          </linearGradient>
          {/* Accent node */}
          <linearGradient id={id('orb')} x1="26" y1="7" x2="33" y2="15" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#c9fbff" />
            <stop offset="100%" stopColor="#2bbede" />
          </linearGradient>
          {/* Top specular sweep */}
          <linearGradient id={id('spec')} x1="8" y1="6" x2="30" y2="13" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#ffffff" stopOpacity="0.82" />
            <stop offset="100%" stopColor="#ffffff" stopOpacity="0" />
          </linearGradient>
          {/* Hover sheen */}
          <linearGradient id={id('sheen')} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="#ffffff" stopOpacity="0" />
            <stop offset="50%" stopColor="#ffffff" stopOpacity="0.55" />
            <stop offset="100%" stopColor="#ffffff" stopOpacity="0" />
          </linearGradient>

          {/* Fine film grain for material texture */}
          <filter id={id('grain')} x="0" y="0" width="100%" height="100%">
            <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" stitchTiles="stitch" result="n" />
            <feColorMatrix in="n" type="saturate" values="0" />
          </filter>

          {/* Squircle clip for overlays */}
          <clipPath id={id('clip')}>
            <rect x="6" y="6" width="28" height="28" rx="9.5" />
          </clipPath>
        </defs>

        {/* 1 — extruded thickness */}
        <rect x="6" y="6" width="28" height="28" rx="9.5" fill={`url(#${id('wall')})`} transform="translate(1.6 2.1)" opacity={inverse ? 0.95 : 0.92} />

        {/* 2 — glass front face */}
        <rect x="6" y="6" width="28" height="28" rx="9.5" fill={`url(#${id('face')})`} />
        <rect x="6" y="6" width="28" height="28" rx="9.5" fill={`url(#${id('bloom')})`} />

        <g clipPath={`url(#${id('clip')})`}>
          {/* 3 — grain + specular */}
          <rect x="6" y="6" width="28" height="28" filter={`url(#${id('grain')})`} opacity="0.13" style={{ mixBlendMode: 'overlay' }} />
          <path d="M6 13 Q20 4 34 11 L34 6 L6 6 Z" fill={`url(#${id('spec')})`} opacity="0.9" />

          {/* 4 — extruded "K" ribbon */}
          {/* depth pass */}
          <path d="M15.6 12.6 V27.4 M15.6 20 L26.6 12.6 M15.6 20 L26.6 27.4"
            stroke="#0a1850" strokeWidth="3.1" strokeLinecap="round" strokeLinejoin="round"
            transform="translate(0.85 1.15)" opacity="0.85" />
          {/* front pass */}
          <path d="M15.6 12.6 V27.4 M15.6 20 L26.6 12.6 M15.6 20 L26.6 27.4"
            stroke={`url(#${id('k')})`} strokeWidth="3.1" strokeLinecap="round" strokeLinejoin="round" />
          {/* top-edge bevel highlight */}
          <path d="M15.6 12.6 V27.4 M15.6 20 L26.6 12.6 M15.6 20 L26.6 27.4"
            stroke="#ffffff" strokeWidth="0.7" strokeLinecap="round" strokeLinejoin="round"
            transform="translate(-0.35 -0.45)" opacity="0.55" />

          {/* 6 — hover sheen (clipped to face) */}
          <rect className="kx-sheen" x="-22" y="6" width="16" height="28" fill={`url(#${id('sheen')})`} opacity="0" />
        </g>

        {/* 5 — accent node */}
        <circle cx="29.6" cy="10.8" r="3.1" fill={`url(#${id('orb')})`} />
        <circle cx="29.6" cy="10.8" r="3.1" fill="none" stroke="#ffffff" strokeOpacity="0.5" strokeWidth="0.5" />
        <circle cx="28.7" cy="9.9" r="0.95" fill="#ffffff" fillOpacity="0.8" />

        {/* glass rim, drawn last so it crowns everything */}
        <rect x="6" y="6" width="28" height="28" rx="9.5" fill="none" stroke={`url(#${id('rim')})`} strokeWidth="0.9" />

        <style>{`
          .kx-logo { transition: transform .45s cubic-bezier(.2,.8,.2,1), filter .45s ease; transform-origin: 50% 60%; }
          .kx-logo:hover { transform: translateY(-1px) scale(1.035) rotate(-1.2deg); }
          .kx-logo .kx-sheen { transition: transform .7s cubic-bezier(.2,.7,.2,1), opacity .2s ease; }
          .kx-logo:hover .kx-sheen { transform: translateX(58px); opacity: 1; }
          @media (prefers-reduced-motion: reduce) {
            .kx-logo, .kx-logo:hover { transition: none; transform: none; }
            .kx-logo .kx-sheen { display: none; }
          }
        `}</style>
      </svg>

      {!collapsed && (
        <div className="min-w-0 leading-none">
          <p className={`truncate text-[15px] font-bold tracking-tight ${inverse ? 'text-white' : 'text-slate-900 dark:text-white'}`}>
            Kynex<span className={`font-black ${inverse ? 'text-cyan-300' : 'text-blue-500 dark:text-cyan-300'}`}>One</span>
          </p>
          <p className={`truncate text-[10px] font-medium tracking-wide ${inverse ? 'text-white/60' : 'text-slate-400 dark:text-slate-500'}`}>
            Workforce Platform
          </p>
        </div>
      )}
    </div>
  );
}
