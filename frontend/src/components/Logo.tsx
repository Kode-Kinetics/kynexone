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
        className={`shrink-0 transition-transform duration-200 hover:scale-105 ${
          inverse ? 'drop-shadow-[0_12px_22px_rgba(37,99,235,0.28)]' : ''
        }`}
        aria-hidden="true"
      >
        {/* Base with richer gradient */}
        <rect width="32" height="32" rx="9" fill="url(#logo-bg)" />

        {/* Inner glow from top-left */}
        <rect width="32" height="32" rx="9" fill="url(#logo-glow)" opacity="0.6" />

        {/* Subtle grid dots */}
        <circle cx="9"  cy="9"  r="1" fill="white" fillOpacity="0.15" />
        <circle cx="16" cy="9"  r="1" fill="white" fillOpacity="0.15" />
        <circle cx="23" cy="9"  r="1" fill="white" fillOpacity="0.15" />
        <circle cx="9"  cy="16" r="1" fill="white" fillOpacity="0.15" />
        <circle cx="23" cy="16" r="1" fill="white" fillOpacity="0.15" />
        <circle cx="9"  cy="23" r="1" fill="white" fillOpacity="0.15" />
        <circle cx="16" cy="23" r="1" fill="white" fillOpacity="0.15" />
        <circle cx="23" cy="23" r="1" fill="white" fillOpacity="0.15" />

        {/* K letterform */}
        <path
          d="M10 9.5V22.5M10 16L20.5 9.5M10 16L20.5 22.5"
          stroke="white"
          strokeWidth="2.6"
          strokeLinecap="round"
          strokeLinejoin="round"
        />

        {/* Accent dot — vibrant cyan */}
        <circle cx="23.5" cy="8.5" r="2.8" fill="url(#logo-dot)" />

        <defs>
          <linearGradient id="logo-bg" x1="0" y1="0" x2="32" y2="32" gradientUnits="userSpaceOnUse">
            <stop offset="0%"   stopColor="#1a3a8f" />
            <stop offset="55%"  stopColor="#2563eb" />
            <stop offset="100%" stopColor="#1d4ed8" />
          </linearGradient>
          <radialGradient id="logo-glow" cx="25%" cy="20%" r="70%" gradientUnits="objectBoundingBox">
            <stop offset="0%"   stopColor="#93c5fd" stopOpacity="0.4" />
            <stop offset="100%" stopColor="#93c5fd" stopOpacity="0" />
          </radialGradient>
          <linearGradient id="logo-dot" x1="21" y1="6" x2="26.5" y2="11.5" gradientUnits="userSpaceOnUse">
            <stop offset="0%"   stopColor="#67e8f9" />
            <stop offset="100%" stopColor="#0891b2" />
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
