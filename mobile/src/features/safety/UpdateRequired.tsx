import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { LiquidBackdrop, LiquidButton, StateCard } from '@/components/ui';
import { useAuthStore } from '@/auth/authStore';
import { useCompatibilityStore } from '@/safety/compatibilityStore';
import { useTheme } from '@/theme/ThemeProvider';

/** Full screen: the session itself could not be read by this app version. */
export function UpdateRequiredScreen() {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const logout = useAuthStore((s) => s.logout);
  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop />
      <StateCard variant="error" title={t('safety.update.title')} body={t('safety.update.body')} testID="update-required" />
      <LiquidButton label={t('safety.offline.signOut')} variant="secondary" onPress={() => void logout()} />
    </View>
  );
}

/** Non-blocking strip: one section could not be read; the rest of the app still works. */
export function UpdateRequiredBanner() {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const updateRequired = useCompatibilityStore((s) => s.updateRequired);
  const dismissed = useCompatibilityStore((s) => s.dismissed);
  const dismiss = useCompatibilityStore((s) => s.dismiss);
  if (!updateRequired || dismissed) return null;
  return (
    <View
      accessibilityRole="alert"
      style={[styles.banner, { top: insets.top, backgroundColor: theme.colors.status.warning.fill }]}
      testID="update-banner"
    >
      <View style={styles.bannerText}>
        <Text style={styles.bannerTitle}>{t('safety.update.title')}</Text>
        <Text style={styles.bannerBody}>{t('safety.update.degraded')}</Text>
      </View>
      <Pressable accessibilityRole="button" accessibilityLabel={t('safety.update.dismiss')} onPress={dismiss} hitSlop={12} style={styles.dismiss}>
        <Text style={styles.bannerTitle}>{t('safety.update.dismiss')}</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, justifyContent: 'center', padding: 24, gap: 16 },
  banner: {
    position: 'absolute', start: 12, end: 12, zIndex: 50, flexDirection: 'row', alignItems: 'center',
    gap: 12, padding: 12, borderRadius: 14,
  },
  bannerText: { flex: 1 },
  bannerTitle: { color: '#111827', fontWeight: '700', fontSize: 14 },
  bannerBody: { color: '#111827', fontSize: 13, marginTop: 2 },
  dismiss: { minHeight: 44, justifyContent: 'center', paddingHorizontal: 8 },
});
