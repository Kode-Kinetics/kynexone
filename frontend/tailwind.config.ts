import type { Config } from 'tailwindcss';

/**
 * Design tokens live in src/styles/tokens.css (CSS variables, light + dark). This file maps them
 * to utilities. See DESIGN.md "Design tokens" for the rules.
 *
 *   type      text-caption 11 / meta 12 / body-sm 13 / body 14 / h4 16 / h3 18 / h2 22 / h1 28 / kpi 28
 *   weight    regular 400, medium 500, semibold 600 (bold and heavier alias to semibold)
 *   space     Tailwind's 4px scale
 *   radius    4 (rounded) / 8 (rounded-lg) / 12 (rounded-xl) / 16 (rounded-2xl) / rounded-full
 *   elevation shadow-e1 / e2 / e3 (neutral ink only)
 *   control   h-control-sm 28 / h-control-md 36 / h-control-lg 40 (44 on coarse pointers)
 */
const role = (name: string) => `rgb(var(--kx-${name}) / <alpha-value>)`;

// Palette steps that are too faint to be text, remapped for text and placeholders only.
const textRemap = {
  slate: { 400: role('t-slate-400'), 500: role('t-slate-500') },
  emerald: { 600: role('t-emerald-600') },
  amber: { 600: role('t-amber-600') },
  rose: { 500: role('t-rose-500'), 600: role('t-rose-600') },
  sapphire: role('t-sapphire'),
};

export default {
  darkMode: 'class',
  content: ['./app/**/*.{ts,tsx}', './src/**/*.{ts,tsx}'],
  theme: {
    fontWeight: {
      thin: '400',
      extralight: '400',
      light: '400',
      normal: '400',
      medium: '500',
      semibold: '600',
      bold: '600',
      extrabold: '600',
      black: '600',
    },
    borderRadius: {
      none: '0px',
      sm: '4px',
      DEFAULT: '4px',
      md: '8px',
      lg: '8px',
      xl: '12px',
      '2xl': '16px',
      '3xl': '16px',
      full: '9999px',
    },
    extend: {
      colors: {
        midnight: '#0B1020',
        sapphire: '#2F6BFF',
        cyanAccent: '#5EEBFF',
        darkSlate: '#111827',
        lightBg: '#F8FAFC',
        emeraldZ: '#00C896',
        sidebarDark: '#0D1221',
        // Semantic roles. Prefer these over palette names in new and shared code.
        canvas: role('canvas'),
        surface: {
          DEFAULT: role('surface'),
          sunken: role('surface-sunken'),
          overlay: role('overlay'),
        },
        fg: {
          DEFAULT: role('text'),
          muted: role('text-muted'),
          subtle: role('text-subtle'),
          inverse: role('text-inverse'),
        },
        line: { DEFAULT: role('border'), strong: role('border-strong') },
        primary: { DEFAULT: role('primary'), hover: role('primary-hover'), fg: role('primary-fg'), soft: role('primary-soft') },
        success: { DEFAULT: role('success'), fg: role('success-fg'), soft: role('success-soft') },
        warning: { DEFAULT: role('warning'), fg: role('warning-fg'), soft: role('warning-soft') },
        danger: { DEFAULT: role('danger'), fg: role('danger-fg'), soft: role('danger-soft') },
        info: { DEFAULT: role('info'), fg: role('info-fg'), soft: role('info-soft') },
        'on-primary': role('on-primary'),
        'on-success': role('on-success'),
        'on-warning': role('on-warning'),
        'on-danger': role('on-danger'),
        'on-info': role('on-info'),
        focus: role('focus'),
      },
      textColor: textRemap,
      placeholderColor: textRemap,
      fontSize: {
        caption: ['0.6875rem', { lineHeight: '1rem' }],
        meta: ['0.75rem', { lineHeight: '1rem' }],
        'body-sm': ['0.8125rem', { lineHeight: '1.25rem' }],
        body: ['0.875rem', { lineHeight: '1.25rem' }],
        h4: ['1rem', { lineHeight: '1.5rem' }],
        h3: ['1.125rem', { lineHeight: '1.75rem' }],
        h2: ['1.375rem', { lineHeight: '1.75rem', letterSpacing: '-0.01em' }],
        h1: ['1.75rem', { lineHeight: '2.125rem', letterSpacing: '-0.02em' }],
        kpi: ['1.75rem', { lineHeight: '2rem', letterSpacing: '-0.02em' }],
      },
      spacing: {
        'control-sm': 'var(--kx-control-sm)',
        'control-md': 'var(--kx-control-md)',
        'control-lg': 'var(--kx-control-lg)',
      },
      boxShadow: {
        // Four levels, neutral ink only. e0 is a 1px line (border), not a shadow.
        e1: 'var(--kx-e1)',
        e2: 'var(--kx-e2)',
        e3: 'var(--kx-e3)',
        // Legacy names, mapped onto the levels so existing call sites converge.
        soft: 'var(--kx-e2)',
        'soft-md': 'var(--kx-e2)',
        panel: 'var(--kx-e1)',
        kpi: 'var(--kx-e1)',
        // Login aurora only.
        glow: '0 0 0 1px rgba(94, 235, 255, 0.20), 0 8px 32px rgba(47, 107, 255, 0.20)',
        'glow-sm': '0 0 0 1px rgba(47, 107, 255, 0.22)',
      },
      fontFamily: {
        // --font-plex-arabic (IBM Plex Sans Arabic, vendored in app/fonts and set on <html> by
        // the root layout) sits after Geist so Latin text is unchanged and only the codepoints
        // Geist cannot draw — i.e. Arabic — fall through to it. This is a font *stack*, not a
        // locale switch: mixed Arabic/Latin strings render correctly in both directions
        // without needing a separate `font-ar` class anywhere.
        sans: ['var(--font-geist)', 'var(--font-plex-arabic)', 'ui-sans-serif', 'system-ui', 'sans-serif'],
        mono: ['var(--font-geist-mono)', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
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
