import React, { useEffect } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { startConnectivityMonitor, useConnectivityStore } from '@/net/connectivity';
import { useTheme } from '@/theme/ThemeProvider';

/** A thin strip at the top while the phone is offline. Information only: nothing is blocked. */
export function OfflineBanner() {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const offline = useConnectivityStore((s) => s.offline);

  useEffect(() => startConnectivityMonitor(), []);

  if (!offline) return null;
  return (
    <View
      accessibilityRole="alert"
      accessibilityLabel={t('safety.offline.bannerA11y')}
      accessibilityLiveRegion="polite"
      pointerEvents="none"
      style={[styles.bar, { paddingTop: insets.top + 4, backgroundColor: theme.colors.status.warning.fill }]}
      testID="offline-banner"
    >
      <Ionicons name="cloud-offline-outline" size={16} color="#111827" />
      <Text style={styles.text}>{t('safety.offline.banner')}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: { position: 'absolute', top: 0, start: 0, end: 0, zIndex: 40, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8, paddingBottom: 6, paddingHorizontal: 12 },
  text: { color: '#111827', fontSize: 13, fontWeight: '600', flexShrink: 1 },
});
