import React, { useCallback, useEffect, useRef, useState } from 'react';
import { AppState, type AppStateStatus, StyleSheet, Text, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { LiquidButton } from '@/components/ui';
import { useAuthStore } from '@/auth/authStore';
import { useAppLockStore } from '@/safety/appLockStore';
import { shouldLockOnColdStart, shouldLockOnResume } from '@/safety/appLockPolicy';
import { useTheme } from '@/theme/ThemeProvider';

/**
 * Covers the app with a lock after it has been in the background for the chosen time (and on a
 * cold start that restored a session), and with an opaque privacy cover whenever the app is not
 * active so the app switcher never shows payslips, loans or the profile.
 */
export function AppLockGate() {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const isInitialized = useAuthStore((s) => s.isInitialized);
  const { prefs, hydrated, canAuthenticate, locked, failed, hydrate, lock, unlock } = useAppLockStore();
  const [appState, setAppState] = useState<AppStateStatus>(AppState.currentState);
  const backgroundedAt = useRef<number | null>(null);
  const coldStartHandled = useRef(false);
  const prompting = useRef(false);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  // Cold start: if the session was restored from the device (not typed in), ask before showing it.
  useEffect(() => {
    if (!hydrated || !isInitialized || coldStartHandled.current) return;
    coldStartHandled.current = true;
    if (shouldLockOnColdStart(prefs, canAuthenticate, isAuthenticated)) lock();
  }, [hydrated, isInitialized, isAuthenticated, prefs, canAuthenticate, lock]);

  useEffect(() => {
    const sub = AppState.addEventListener('change', (next) => {
      setAppState(next);
      if (next === 'background') backgroundedAt.current = Date.now();
      if (next === 'active') {
        const s = useAppLockStore.getState();
        if (
          shouldLockOnResume({
            prefs: s.prefs,
            canAuthenticate: s.canAuthenticate,
            signedIn: useAuthStore.getState().isAuthenticated,
            backgroundedAt: backgroundedAt.current,
            now: Date.now(),
          })
        ) {
          s.lock();
        }
        backgroundedAt.current = null;
      }
    });
    return () => sub.remove();
  }, []);

  const verify = useCallback(async () => {
    if (prompting.current) return;
    prompting.current = true;
    try {
      await unlock(t('safety.lock.prompt'), t('common.cancel'));
    } finally {
      prompting.current = false;
    }
  }, [unlock, t]);

  // Ask as soon as the lock is showing and the app is in front; the button covers a cancelled prompt.
  useEffect(() => {
    if (locked && appState === 'active') void verify();
  }, [locked, appState, verify]);

  const cover = isAuthenticated && prefs.enabled && appState !== 'active';
  if (!locked && !cover) return null;

  return (
    <View
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      accessibilityViewIsModal
      importantForAccessibility="yes"
      testID={locked ? 'app-lock' : 'privacy-cover'}
    >
      {locked ? (
        <View style={styles.card} accessible accessibilityLabel={t('safety.lock.a11yLocked')}>
          <Text style={[theme.typography.title2, styles.center, { color: theme.colors.text }]} accessibilityRole="header">
            {t('safety.lock.title')}
          </Text>
          <Text style={[theme.typography.body, styles.center, { color: theme.colors.textSecondary }]}>
            {t('safety.lock.body')}
          </Text>
          {failed ? (
            <Text accessibilityLiveRegion="polite" style={[theme.typography.footnote, styles.center, { color: theme.colors.danger }]}>
              {t('safety.lock.failed')}
            </Text>
          ) : null}
          <LiquidButton label={t('safety.lock.unlock')} onPress={() => void verify()} />
        </View>
      ) : (
        <View accessible={false} importantForAccessibility="no-hide-descendants" />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { position: "absolute", top: 0, bottom: 0, start: 0, end: 0, zIndex: 1000, elevation: 1000, alignItems: 'center', justifyContent: 'center', padding: 24 },
  card: { width: '100%', maxWidth: 340, gap: 16 },
  center: { textAlign: 'center' },
});
