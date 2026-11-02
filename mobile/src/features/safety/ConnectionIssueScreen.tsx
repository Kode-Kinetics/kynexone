import React, { useCallback, useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { LiquidBackdrop, LiquidButton, StateCard } from '@/components/ui';
import { useAuthStore } from '@/auth/authStore';
import { useTheme } from '@/theme/ThemeProvider';
import { backoffDelayMs } from '@/api/sessionErrors';

/**
 * Shown when the stored session could not be checked because the network (or the server) was
 * unreachable. The tokens are still on the device; this screen retries with backoff and lets the
 * user retry now or sign out. It never asks for a password.
 */
export function ConnectionIssueScreen() {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const initialize = useAuthStore((s) => s.initialize);
  const logout = useAuthStore((s) => s.logout);
  const [attempt, setAttempt] = useState(0);
  const [seconds, setSeconds] = useState(() => Math.ceil(backoffDelayMs(0, 0, 2000) / 1000));

  const retry = useCallback(() => {
    setAttempt((n) => n + 1);
    void initialize();
  }, [initialize]);

  // The screen unmounts when initialize() succeeds; if it fails again, `attempt` changes and the
  // countdown restarts with a longer wait (2s, 4s, 8s ... up to 30s).
  useEffect(() => {
    const total = Math.ceil(backoffDelayMs(attempt, 0, 2000) / 1000);
    setSeconds(total);
    const timer = setInterval(() => setSeconds((s) => Math.max(0, s - 1)), 1000);
    return () => clearInterval(timer);
  }, [attempt]);

  useEffect(() => {
    if (seconds === 0) retry();
  }, [seconds, retry]);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop />
      <StateCard
        variant="offline"
        title={t('safety.offline.sessionTitle')}
        body={`${t('safety.offline.sessionBody')}\n${t('safety.offline.retryingIn', { seconds })}`}
        onRetry={retry}
        retryLabel={t('safety.offline.retryNow')}
        testID="connection-issue"
      />
      <LiquidButton label={t('safety.offline.signOut')} variant="secondary" onPress={() => void logout()} />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, justifyContent: 'center', padding: 24, gap: 16 },
});
