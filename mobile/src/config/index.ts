// ============================================================
// ZAYRA MOBILE — App Configuration
// ============================================================

import Constants from 'expo-constants';

const configuredApiBaseUrl =
  process.env.EXPO_PUBLIC_API_BASE_URL ||
  (Constants.expoConfig?.extra?.apiBaseUrl as string | undefined) ||
  'http://localhost:5117/api';

const ENV = {
  development: {
    API_BASE_URL: configuredApiBaseUrl,
    TENANT_HEADER: 'X-Tenant-Id',
    PUSH_NOTIFICATIONS_ENABLED: true,
    GEOFENCE_ENABLED: true,
    BIOMETRIC_ENABLED: true,
  },
  staging: {
    API_BASE_URL: configuredApiBaseUrl,
    TENANT_HEADER: 'X-Tenant-Id',
    PUSH_NOTIFICATIONS_ENABLED: true,
    GEOFENCE_ENABLED: true,
    BIOMETRIC_ENABLED: true,
  },
  production: {
    API_BASE_URL: configuredApiBaseUrl,
    TENANT_HEADER: 'X-Tenant-Id',
    PUSH_NOTIFICATIONS_ENABLED: true,
    GEOFENCE_ENABLED: true,
    BIOMETRIC_ENABLED: true,
  },
};

type EnvKey = keyof typeof ENV;

function getEnv(): EnvKey {
  const releaseChannel = Constants.expoConfig?.extra?.releaseChannel as string | undefined;
  if (releaseChannel?.startsWith('prod')) return 'production';
  if (releaseChannel?.startsWith('staging')) return 'staging';
  return 'development';
}

export const APP_CONFIG = ENV[getEnv()];

export const APP_VERSION = Constants.expoConfig?.version ?? '1.0.0';

// Storage keys
export const STORAGE_KEYS = {
  ACCESS_TOKEN: 'zayra_access_token',
  REFRESH_TOKEN: 'zayra_refresh_token',
  TOKEN_EXPIRY: 'zayra_token_expiry',
  USER: 'zayra_user',
  DEVICE_ID: 'zayra_device_id',
  PUSH_TOKEN: 'zayra_push_token',
  LANGUAGE: 'zayra_language',
  THEME: 'zayra_theme',
  BIOMETRIC_ENABLED: 'zayra_biometric_enabled',
  OFFLINE_PUNCHES: 'zayra_offline_punches',
} as const;

// Token expiry buffer: refresh 5 minutes before expiry
export const TOKEN_REFRESH_BUFFER_MS = 5 * 60 * 1000;

// Request timeout
export const API_TIMEOUT_MS = 30_000;

// Geofence default radius (meters)
export const DEFAULT_GEOFENCE_RADIUS = 200;

// App color scheme
export const COLORS = {
  navy: '#0B1020',
  blue: '#2F6BFF',
  cyan: '#5EEBFF',
  bg: '#F8FAFC',
  background: '#F8FAFC', // alias for bg
  success: '#00C896',
  emerald: '#00C896',    // alias for success
  warning: '#F59E0B',
  error: '#EF4444',
  white: '#FFFFFF',
  card: '#FFFFFF',
  border: '#E2E8F0',
  text: '#0F172A',
  textSecondary: '#475569',
  muted: '#94A3B8',
  darkBg: '#0B1020',
  darkCard: '#141929',
  darkBorder: '#1E2A45',
  darkText: '#F1F5F9',
  darkMuted: '#64748B',
} as const;

export const FONT_SIZES = {
  xs: 11,
  sm: 13,
  base: 15,
  md: 17,
  lg: 20,
  xl: 24,
  '2xl': 28,
  '3xl': 34,
} as const;

export const SPACING = {
  xs: 4,
  sm: 8,
  md: 12,
  base: 16,
  lg: 20,
  xl: 24,
  '2xl': 32,
  '3xl': 40,
  '4xl': 48,
} as const;

export const BORDER_RADIUS = {
  sm: 6,
  md: 10,
  lg: 16,
  xl: 20,
  full: 9999,
} as const;
