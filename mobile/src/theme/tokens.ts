import { Platform, type TextStyle } from 'react-native';
import { motion } from './motion';

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedThemeMode = 'light' | 'dark';
export type ThemeLanguage = 'en' | 'ar';
export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

// ------------------------------------------------------------------
// Colour
//
// Text-safe roles (>= 4.5:1 on canvas and on a card in their mode):
//   text, textSecondary, textMuted, accent, status.<tone>.fg
// Fills that carry white text (>= 4.5:1): primaryFill, status.<tone>.fill
//
// Legacy keys (primary, success, warning, danger, info, cyan, violet) are kept so
// every existing screen compiles. In light mode they are remapped to the deeper,
// text-safe value; new code uses accent / status instead.
// ------------------------------------------------------------------

const legacyDark = {
  primary: '#4F75FF',
  primaryStrong: '#3157E8',
  cyan: '#48DDF6',
  violet: '#9067F9',
  success: '#10B981',
  warning: '#F59E0B',
  danger: '#F43F5E',
  info: '#0EA5E9',
};

const legacyLight: typeof legacyDark = {
  primary: '#3157E8',
  primaryStrong: '#2A4CD0',
  cyan: '#0E7490',
  violet: '#6D28D9',
  success: '#047857',
  warning: '#B45309',
  danger: '#BE123C',
  info: '#0369A1',
};

/** Solid fills that keep white text >= 4.5:1 in both modes. */
const fills = {
  primary: '#2F5BEA',
  success: '#047857',
  warning: '#B45309',
  danger: '#BE123C',
  info: '#0369A1',
};

function tone(fg: string, fill: string) {
  // tint = fg at ~12% (hex alpha 1F)
  return { fg, fill, tint: `${fg}1F` };
}

const lightStatus = {
  success: tone('#047857', fills.success),
  warning: tone('#B45309', fills.warning),
  danger: tone('#BE123C', fills.danger),
  info: tone('#0369A1', fills.info),
  neutral: { fg: '#43506A', fill: '#43506A', tint: 'rgba(67,80,106,0.10)' },
};

const darkStatus: typeof lightStatus = {
  success: tone('#34D399', fills.success),
  warning: tone('#FBBF24', fills.warning),
  danger: tone('#FB7185', fills.danger),
  info: tone('#38BDF8', fills.info),
  neutral: { fg: '#C5CEE0', fill: '#43506A', tint: 'rgba(197,206,224,0.12)' },
};

const lightColors = {
  ...legacyLight,
  /** The one brand colour for text and icons. */
  accent: '#3157E8',
  /** Brand fill behind white text. */
  primaryFill: fills.primary,
  onFill: '#FFFFFF',
  canvas: '#EEF4FC',
  canvasAlt: '#DCE9F8',
  canvasDeep: '#C9DCF4',
  // Solid content surfaces (Surface level 0 / 1 / 2).
  surfaceInset: '#E6EEF8',
  card: '#FFFFFF',
  cardRaised: '#FFFFFF',
  hairline: 'rgba(15,30,60,0.10)',
  // Translucent legacy surfaces (chips, overlays) — kept for existing screens.
  surface: 'rgba(255,255,255,0.72)',
  surfaceStrong: 'rgba(255,255,255,0.92)',
  surfaceSoft: 'rgba(255,255,255,0.48)',
  surfaceMuted: 'rgba(231,239,249,0.86)',
  glassTint: 'rgba(255,255,255,0.32)',
  glassBorder: 'rgba(255,255,255,0.88)',
  glassHighlight: 'rgba(255,255,255,0.72)',
  /** Solid stand-in for glass under Reduce Transparency and on Android. */
  chromeSolid: '#F8FAFE',
  text: '#0B1020',
  textSecondary: '#43506A',
  textMuted: '#5B6880',
  border: 'rgba(70,96,135,0.14)',
  divider: 'rgba(70,96,135,0.11)',
  overlay: 'rgba(5,12,28,0.38)',
  scrim: 'rgba(5,12,28,0.42)',
  shadow: '#254D7A',
  skeleton: 'rgba(67,80,106,0.10)',
  status: lightStatus,
};

const darkColors: typeof lightColors = {
  ...legacyDark,
  accent: '#7C9BFF',
  primaryFill: fills.primary,
  onFill: '#FFFFFF',
  canvas: '#050814',
  canvasAlt: '#081223',
  canvasDeep: '#0C1B34',
  surfaceInset: '#0A1324',
  card: '#101C33',
  cardRaised: '#15233E',
  hairline: 'rgba(255,255,255,0.09)',
  surface: 'rgba(10,20,39,0.70)',
  surfaceStrong: 'rgba(12,25,48,0.90)',
  surfaceSoft: 'rgba(255,255,255,0.055)',
  surfaceMuted: 'rgba(20,37,66,0.84)',
  glassTint: 'rgba(40,93,213,0.16)',
  glassBorder: 'rgba(255,255,255,0.16)',
  glassHighlight: 'rgba(255,255,255,0.22)',
  chromeSolid: '#0E1830',
  text: '#F7FAFF',
  textSecondary: '#C5CEE0',
  textMuted: '#8997AF',
  border: 'rgba(169,194,232,0.14)',
  divider: 'rgba(169,194,232,0.10)',
  overlay: 'rgba(0,0,0,0.52)',
  scrim: 'rgba(0,0,0,0.56)',
  shadow: '#00040D',
  skeleton: 'rgba(197,206,224,0.10)',
  status: darkStatus,
};

// ------------------------------------------------------------------
// Spacing / radius
// ------------------------------------------------------------------

const spacing = {
  xxs: 2,
  xs: 4,
  sm: 8,
  md: 12,
  base: 16,
  lg: 20,
  xl: 24,
  xxl: 32,
  xxxl: 40,
};

const radius = {
  xs: 8,
  sm: 12,
  md: 16,
  lg: 22,
  xl: 28,
  /** Legacy alias of xl. */
  xxl: 28,
  pill: 999,
};

/** Inner radius that stays concentric with an outer shape inset by `padding`. */
export function concentric(outer: number, padding: number): number {
  return Math.max(0, outer - padding);
}

// ------------------------------------------------------------------
// Typography — locale-aware. Arabic: no tracking (it breaks letter joins),
// 15% more line height, never uppercase.
// ------------------------------------------------------------------

export type TypeStyle = Pick<TextStyle, 'fontSize' | 'lineHeight' | 'fontWeight' | 'letterSpacing' | 'textTransform'>;

const baseType = {
  // iOS text styles
  largeTitle: { fontSize: 34, lineHeight: 41, fontWeight: '700', letterSpacing: 0.37 },
  title1: { fontSize: 28, lineHeight: 34, fontWeight: '700', letterSpacing: 0.36 },
  title2: { fontSize: 22, lineHeight: 28, fontWeight: '700', letterSpacing: 0.35 },
  title3: { fontSize: 20, lineHeight: 25, fontWeight: '600', letterSpacing: 0.38 },
  headline: { fontSize: 17, lineHeight: 22, fontWeight: '600', letterSpacing: -0.41 },
  callout: { fontSize: 16, lineHeight: 21, fontWeight: '400', letterSpacing: -0.32 },
  subheadline: { fontSize: 15, lineHeight: 20, fontWeight: '400', letterSpacing: -0.24 },
  footnote: { fontSize: 13, lineHeight: 18, fontWeight: '400', letterSpacing: -0.08 },
  eyebrow: { fontSize: 12, lineHeight: 16, fontWeight: '600', letterSpacing: 0.6, textTransform: 'uppercase' },
  // Legacy keys (existing screens) — `body` and `caption` keep their old metrics.
  display: { fontSize: 32, lineHeight: 38, fontWeight: '800', letterSpacing: -0.8 },
  h1: { fontSize: 26, lineHeight: 32, fontWeight: '800', letterSpacing: -0.45 },
  h2: { fontSize: 21, lineHeight: 27, fontWeight: '700', letterSpacing: -0.25 },
  h3: { fontSize: 17, lineHeight: 23, fontWeight: '700' },
  body: { fontSize: 15, lineHeight: 21, fontWeight: '400' },
  bodyStrong: { fontSize: 15, lineHeight: 21, fontWeight: '600' },
  caption: { fontSize: 12, lineHeight: 17, fontWeight: '500' },
  micro: { fontSize: 11, lineHeight: 15, fontWeight: '600', letterSpacing: 0.15 },
} satisfies Record<string, TypeStyle>;

export type TypeKey = keyof typeof baseType;
export type Typography = { [K in TypeKey]: TypeStyle };

export function createTypography(language: ThemeLanguage): Typography {
  const out = {} as Typography;
  for (const key of Object.keys(baseType) as TypeKey[]) {
    const style: TypeStyle = { ...baseType[key] };
    if (language === 'ar') {
      style.letterSpacing = 0;
      style.textTransform = 'none';
      if (style.lineHeight) style.lineHeight = Math.round(style.lineHeight * 1.15);
    }
    out[key] = style;
  }
  return out;
}

// ------------------------------------------------------------------

export function createTheme(
  mode: ResolvedThemeMode,
  reduceTransparency: boolean,
  language: ThemeLanguage = 'en',
) {
  const isDark = mode === 'dark';
  const colors = isDark ? darkColors : lightColors;
  return {
    mode,
    isDark,
    reduceTransparency,
    language,
    isArabic: language === 'ar',
    colors: {
      ...colors,
      surface: reduceTransparency ? colors.surfaceStrong : colors.surface,
      glassTint: reduceTransparency ? colors.surfaceStrong : colors.glassTint,
    },
    spacing,
    radius,
    typography: createTypography(language),
    motion,
    shadows: {
      soft: {
        shadowColor: colors.shadow,
        shadowOffset: { width: 0, height: 8 },
        shadowOpacity: isDark ? 0.36 : 0.13,
        shadowRadius: 24,
        elevation: 8,
      },
      raised: {
        shadowColor: colors.shadow,
        shadowOffset: { width: 0, height: 4 },
        shadowOpacity: isDark ? 0.32 : 0.08,
        shadowRadius: 12,
        elevation: 3,
      },
      floating: {
        shadowColor: colors.shadow,
        shadowOffset: { width: 0, height: 14 },
        shadowOpacity: isDark ? 0.48 : 0.18,
        shadowRadius: 30,
        elevation: 14,
      },
    },
    fontFamily: Platform.select({ ios: undefined, android: 'sans-serif', default: undefined }),
    gradients: {
      app: isDark
        ? ['#040712', '#09172D', '#0B1F3D'] as const
        : ['#F8FBFF', '#E8F1FC', '#DCEAFE'] as const,
      hero: isDark
        ? ['#0B1630', '#122A54', '#0B1630'] as const
        : ['#FFFFFF', '#E7F0FF', '#D9E8FF'] as const,
      // Legacy two-stop button "gradients" are now solid pairs, so white text keeps
      // >= 4.5:1 across the whole surface.
      primary: [fills.primary, fills.primary] as const,
      accent: ['#4BE4F7', '#5B7CFA', '#926CF8'] as const,
      success: [fills.success, fills.success] as const,
      danger: [fills.danger, fills.danger] as const,
    },
  };
}

export type KynexTheme = ReturnType<typeof createTheme>;
export type ThemeColors = KynexTheme['colors'];
