import React, { useEffect, useMemo } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { NavigationContainer } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { AuthStack } from './AuthStack';
import { MainTabs } from './MainTabs';
import { navigationRef } from './routes';
import { LiquidBackdrop, Surface } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { MfaSetupBanner } from '@/features/auth/MfaSetupBanner';
import { ResetCodeBanner } from '@/features/auth/ResetCodeBanner';

export function RootNavigator() {
  const { isAuthenticated, isInitialized, initialize } = useAuthStore();
  const { theme } = useTheme();
  const { t } = useTranslation();

  useEffect(() => {
    void initialize();
  }, [initialize]);

  const navigationTheme = useMemo(() => ({
    dark: theme.isDark,
    colors: {
      primary: theme.colors.accent,
      background: theme.colors.canvas,
      card: theme.colors.surfaceStrong,
      text: theme.colors.text,
      border: theme.colors.border,
      notification: theme.colors.danger,
    },
  }), [theme]);

  // Only the cold-start session restore may replace the navigator. Sign-in,
  // MFA and sign-out also toggle isLoading; unmounting the tree for those
  // threw away the screen that was about to navigate to the MFA step.
  if (!isInitialized) {
    return (
      <View style={[styles.loading, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop />
        <Surface style={styles.loadingCard} contentStyle={styles.loadingContent} radius={theme.radius.xl}>
          <View style={[styles.mark, { backgroundColor: theme.colors.primaryFill }]}>
            <Text accessible={false} style={styles.markText}>K</Text>
          </View>
          <ActivityIndicator color={theme.colors.accent} size="small" />
          <Text
            accessibilityLiveRegion="polite"
            style={[theme.typography.footnote, styles.center, { color: theme.colors.textSecondary }]}
          >
            {t('kit.preparing')}
          </Text>
        </Surface>
      </View>
    );
  }

  return (
    <NavigationContainer ref={navigationRef} theme={navigationTheme}>
      {isAuthenticated ? <MainTabs /> : <AuthStack />}
      {isAuthenticated ? <MfaSetupBanner /> : null}
      {isAuthenticated ? <ResetCodeBanner /> : null}
    </NavigationContainer>
  );
}

const styles = StyleSheet.create({
  loading: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  loadingCard: { width: '100%', maxWidth: 310, minHeight: 210 },
  loadingContent: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 16, padding: 24 },
  mark: {
    width: 68,
    height: 68,
    borderRadius: 22,
    alignItems: 'center',
    justifyContent: 'center',
  },
  markText: { color: '#FFFFFF', fontSize: 34, fontWeight: '800', letterSpacing: -1 },
  center: { textAlign: 'center' },
});
