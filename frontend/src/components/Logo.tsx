'use client';

interface LogoProps {
  collapsed?: boolean;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  theme?: 'light' | 'dark';
}

export function Logo({ collapsed, size = 'md', theme = 'light' }: LogoProps) {
  const dim = size === 'xl' ? 52 : size === 'lg' ? 44 : size === 'sm' ? 28 : 36;
  const inverse = theme === 'dark';

  return (
    <div className="flex items-center gap-2.5">
      <svg
        width={dim}
        height={dim}
        viewBox="0 0 32 32"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        className={`shrink-0 transition-transform duration-300 hover:scale-[1.04] ${
          inverse
            ? 'drop-shadow-[0_16px_28px_rgba(29,78,216,0.35)]'
            : 'drop-shadow-[0_14px_24px_rgba(37,99,235,0.16)]'
        }`}
        aria-hidden="true"
      >
        <g filter="url(#logo-shadow)">
          {/* Back face creates the 3D offset */}
          <path
            d="M7.5 8.5C7.5 6.01472 9.51472 4 12 4H23.5C26.5376 4 29 6.46243 29 9.5V21.5C29 24.5376 26.5376 27 23.5 27H12C9.51472 27 7.5 24.9853 7.5 22.5V8.5Z"
            fill="url(#logo-side)"
            opacity={inverse ? 0.85 : 0.9}
            transform="translate(1.7 1.9)"
          />

          {/* Main face */}
          <path
            d="M7.5 8.5C7.5 6.01472 9.51472 4 12 4H23.5C26.5376 4 29 6.46243 29 9.5V21.5C29 24.5376 26.5376 27 23.5 27H12C9.51472 27 7.5 24.9853 7.5 22.5V8.5Z"
            fill="url(#logo-bg)"
          />

          {/* Top highlight */}
          <path
            d="M8.5 9C8.5 6.96243 10.1624 5.3 12.2 5.3H22.9C25.0231 5.3 26.8 7.07687 26.8 9.2V10.3H8.5V9Z"
            fill="url(#logo-highlight)"
            opacity={inverse ? 0.95 : 0.9}
          />

          {/* Inner sheen */}
          <rect width="21" height="21" x="8.5" y="5.5" rx="7.5" fill="url(#logo-glow)" opacity="0.7" />

          {/* Subtle grid dots */}
          <circle cx="11" cy="10" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="17" cy="10" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="23" cy="10" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="11" cy="16" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="23" cy="16" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="11" cy="22" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="17" cy="22" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />
          <circle cx="23" cy="22" r="1" fill="white" fillOpacity={inverse ? '0.18' : '0.13'} />

          {/* K letterform */}
          <path
            d="M12 9.5V22.5M12 16L21.6 9.5M12 16L21.6 22.5"
            stroke="white"
            strokeWidth="2.55"
            strokeLinecap="round"
            strokeLinejoin="round"
          />

          {/* Accent dot — vibrant cyan with depth */}
          <circle cx="23.9" cy="8.7" r="3.1" fill="url(#logo-dot)" />
          <circle cx="22.9" cy="7.9" r="1.05" fill="white" fillOpacity="0.7" />
        </g>

        <defs>
          <filter id="logo-shadow" x="-20%" y="-20%" width="160%" height="170%" colorInterpolationFilters="sRGB">
            <feDropShadow dx="0" dy="2" stdDeviation="2" floodColor={inverse ? '#1d4ed8' : '#0f172a'} floodOpacity={inverse ? '0.28' : '0.14'} />
            <feDropShadow dx="0" dy="7" stdDeviation="7" floodColor="#1d4ed8" floodOpacity={inverse ? '0.22' : '0.12'} />
          </filter>
          <linearGradient id="logo-bg" x1="7.5" y1="4" x2="29" y2="27" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#2f6bff" />
            <stop offset="45%" stopColor="#2455d8" />
            <stop offset="100%" stopColor="#143b9f" />
          </linearGradient>
          <linearGradient id="logo-side" x1="9" y1="6" x2="29" y2="28" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#0d2d86" />
            <stop offset="100%" stopColor="#07205f" />
          </linearGradient>
          <linearGradient id="logo-highlight" x1="8.5" y1="5.3" x2="26.8" y2="10.2" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#ffffff" stopOpacity="0.9" />
            <stop offset="100%" stopColor="#ffffff" stopOpacity="0.08" />
          </linearGradient>
          <radialGradient id="logo-glow" cx="25%" cy="20%" r="70%" gradientUnits="objectBoundingBox">
            <stop offset="0%" stopColor="#dbeafe" stopOpacity="0.45" />
            <stop offset="100%" stopColor="#dbeafe" stopOpacity="0" />
          </radialGradient>
          <linearGradient id="logo-dot" x1="20.5" y1="6.2" x2="26.5" y2="12.5" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#8ff7ff" />
            <stop offset="100%" stopColor="#20b8d6" />
          </linearGradient>
        </defs>
      </svg>

      {!collapsed && (
        <div className="min-w-0 leading-none">
          <p className={`truncate text-[15px] font-bold tracking-tight ${inverse ? 'text-white' : 'text-slate-900 dark:text-white'}`}>
            Kynex<span className={`font-black ${inverse ? 'text-cyan-300' : 'text-blue-400'}`}>One</span>
          </p>
          <p className={`truncate text-[10px] font-medium tracking-wide ${inverse ? 'text-white/65' : 'text-slate-400 dark:text-slate-500'}`}>
            Workforce Platform
          </p>
        </div>
      )}
    </div>
  );
}
