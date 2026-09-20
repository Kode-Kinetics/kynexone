import { Platform } from 'react-native';

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedThemeMode = 'light' | 'dark';

const brand = {
  primary: '#4F75FF',
  primaryStrong: '#3157E8',
  cyan: '#48DDF6',
  violet: '#9067F9',
  success: '#10B981',
  warning: '#F59E0B',
  danger: '#F43F5E',
  info: '#0EA5E9',
};

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
  sm: 10,
  md: 14,
  lg: 18,
  xl: 24,
  xxl: 30,
  pill: 999,
};
const typography = {
  display: { fontSize: 32, lineHeight: 38, fontWeight: '800' as const, letterSpacing: -0.8 },
  h1: { fontSize: 26, lineHeight: 32, fontWeight: '800' as const, letterSpacing: -0.45 },
  h2: { fontSize: 21, lineHeight: 27, fontWeight: '700' as const, letterSpacing: -0.25 },
  h3: { fontSize: 17, lineHeight: 23, fontWeight: '700' as const },
  body: { fontSize: 15, lineHeight: 21, fontWeight: '400' as const },
  bodyStrong: { fontSize: 15, lineHeight: 21, fontWeight: '600' as const },
  caption: { fontSize: 12, lineHeight: 17, fontWeight: '500' as const },
  micro: { fontSize: 11, lineHeight: 15, fontWeight: '600' as const, letterSpacing: 0.15 },
};

const lightColors = {
  ...brand,
  canvas: '#EEF4FC',
  canvasAlt: '#DCE9F8',
  canvasDeep: '#C9DCF4',
  surface: 'rgba(255,255,255,0.72)',
  surfaceStrong: 'rgba(255,255,255,0.92)',
  surfaceSoft: 'rgba(255,255,255,0.48)',
  surfaceMuted: 'rgba(231,239,249,0.86)',
  glassTint: 'rgba(255,255,255,0.32)',
  glassBorder: 'rgba(255,255,255,0.88)',
  glassHighlight: 'rgba(255,255,255,0.72)',
  text: '#0B1020',
  textSecondary: '#43506A',
  textMuted: '#74829A',
  border: 'rgba(70,96,135,0.14)',
  divider: 'rgba(70,96,135,0.11)',
  overlay: 'rgba(5,12,28,0.38)',
  shadow: '#254D7A',
};
const darkColors = {
  ...brand,
  canvas: '#050814',
  canvasAlt: '#081223',
  canvasDeep: '#0C1B34',
  surface: 'rgba(10,20,39,0.70)',
  surfaceStrong: 'rgba(12,25,48,0.90)',
  surfaceSoft: 'rgba(255,255,255,0.055)',
  surfaceMuted: 'rgba(20,37,66,0.84)',
  glassTint: 'rgba(40,93,213,0.16)',
  glassBorder: 'rgba(255,255,255,0.16)',
  glassHighlight: 'rgba(255,255,255,0.22)',
  text: '#F7FAFF',
  textSecondary: '#C5CEE0',
  textMuted: '#8997AF',
  border: 'rgba(169,194,232,0.14)',
  divider: 'rgba(169,194,232,0.10)',
  overlay: 'rgba(0,0,0,0.52)',
  shadow: '#00040D',
};

export function createTheme(mode: ResolvedThemeMode, reduceTransparency: boolean) {
  const isDark = mode === 'dark';
  const colors = isDark ? darkColors : lightColors;
  return {
    mode,
    isDark,
    reduceTransparency,
    colors: {
      ...colors,
      surface: reduceTransparency ? colors.surfaceStrong : colors.surface,
      glassTint: reduceTransparency ? colors.surfaceStrong : colors.glassTint,
    },
    spacing,
    radius,
    typography,
    shadows: {
      soft: {
        shadowColor: colors.shadow,
        shadowOffset: { width: 0, height: 8 },
        shadowOpacity: isDark ? 0.36 : 0.13,
        shadowRadius: 24,
        elevation: 8,
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
      primary: ['#5E83FF', '#3A5DEB'] as const,
      accent: ['#4BE4F7', '#5B7CFA', '#926CF8'] as const,
      success: ['#22C997', '#0D9F75'] as const,
      danger: ['#FB6683', '#E43D61'] as const,
    },
  };
}

export type KynexTheme = ReturnType<typeof createTheme>;
