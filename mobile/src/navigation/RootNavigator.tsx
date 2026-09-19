import React, { useEffect, useMemo } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { NavigationContainer } from '@react-navigation/native';
import { useAuthStore } from '@/auth/authStore';
import { AuthStack } from './AuthStack';
import { MainTabs } from './MainTabs';
import { navigationRef } from './routes';
import { GlassSurface, LiquidBackdrop } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

export function RootNavigator() {
  const { isAuthenticated, isLoading, initialize } = useAuthStore();
  const { theme } = useTheme();

  useEffect(() => {
    void initialize();
  }, [initialize]);

  const navigationTheme = useMemo(() => ({
    dark: theme.isDark,
    colors: {
      primary: theme.colors.primary,
      background: theme.colors.canvas,
      card: theme.colors.surfaceStrong,
      text: theme.colors.text,
      border: theme.colors.border,
      notification: theme.colors.danger,
    },
  }), [theme]);

  if (isLoading) {
    return (
      <View style={[styles.loading, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop />
        <GlassSurface style={styles.loadingCard} contentStyle={styles.loadingContent} radius={28}>
          <View style={[styles.mark, { backgroundColor: theme.colors.primary }]}>
            <Text style={styles.markText}>K</Text>
          </View>
          <ActivityIndicator color={theme.colors.cyan} size="small" />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
            Preparing your secure workspace…
          </Text>
        </GlassSurface>
      </View>
    );
  }

  return (
    <NavigationContainer ref={navigationRef} theme={navigationTheme}>
      {isAuthenticated ? <MainTabs /> : <AuthStack />}
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
});
