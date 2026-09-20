import type { Config } from 'tailwindcss';

export default {
  darkMode: 'class',
  content: ['./app/**/*.{ts,tsx}', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        midnight: '#0B1020',
        sapphire: '#2F6BFF',
        cyanAccent: '#5EEBFF',
        darkSlate: '#111827',
        lightBg: '#F8FAFC',
        emeraldZ: '#00C896',
        sidebarDark: '#0D1221',
      },
      boxShadow: {
        soft: '0 4px 24px rgba(15, 23, 42, 0.07)',
        'soft-md': '0 8px 32px rgba(15, 23, 42, 0.10)',
        glow: '0 0 0 1px rgba(94, 235, 255, 0.20), 0 8px 32px rgba(47, 107, 255, 0.20)',
        'glow-sm': '0 0 0 1px rgba(47, 107, 255, 0.22)',
        panel: '0 1px 3px rgba(0,0,0,0.06), 0 1px 2px rgba(0,0,0,0.04)',
        'kpi': '0 2px 8px rgba(15, 23, 42, 0.06)',
      },
      fontFamily: {
        // 'IBM Plex Sans Arabic' sits after Inter so Latin text is unchanged and only the
        // codepoints Inter cannot draw — i.e. Arabic — fall through to it. This is a font
        // *stack*, not a locale switch: mixed Arabic/Latin strings render correctly in both
        // directions without needing a separate `font-ar` class anywhere.
        sans: ['Inter', 'IBM Plex Sans Arabic', 'ui-sans-serif', 'system-ui', 'sans-serif'],
      },
      borderRadius: {
        xl: '12px',
        '2xl': '16px',
      },
      transitionProperty: {
        width: 'width',
      },
      keyframes: {
        'fade-in': {
          '0%':   { opacity: '0', transform: 'translateY(6px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'fade-in-fast': {
          '0%':   { opacity: '0' },
          '100%': { opacity: '1' },
        },
        'slide-in-left': {
          '0%':   { opacity: '0', transform: 'translateX(-8px)' },
          '100%': { opacity: '1', transform: 'translateX(0)' },
        },
        'scale-in': {
          '0%':   { opacity: '0', transform: 'scale(0.96)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
        'slide-in-right': {
          '0%':   { opacity: '0', transform: 'translateX(16px)' },
          '100%': { opacity: '1', transform: 'translateX(0)' },
        },
      },
      animation: {
        'fade-in':         'fade-in 0.22s ease-out both',
        'fade-in-fast':    'fade-in-fast 0.15s ease-out both',
        'slide-in-left':   'slide-in-left 0.20s ease-out both',
        'scale-in':        'scale-in 0.18s ease-out both',
        'slide-in-right':  'slide-in-right 0.22s ease-out both',
      },
    },
  },
  plugins: [],
} satisfies Config;
