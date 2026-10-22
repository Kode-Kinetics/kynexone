// ============================================================
// KynexOne Mobile — App root
// ============================================================

import i18n, { promptRestartIfNeeded } from '@/config/i18n'; // Initialize i18n before anything renders
import React, { useEffect } from 'react';
import { Alert, InteractionManager } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { RootNavigator } from '@/navigation/RootNavigator';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { ScreenTour, TOUR_MODE, registerBoundaryReset } from '@/dev/ScreenTour';
import { useAuthStore } from '@/auth/authStore';
import { navigationRef } from '@/navigation/routes';
import { useSnapshotProtection } from '@/security/useSnapshotProtection';
import { ThemeProvider, useTheme } from '@/theme/ThemeProvider';
import {
  setupNotificationListeners,
  getLastNotificationResponseAsync,
} from '@/features/notifications/pushNotifications';
import {
  consumeReadyIntent,
  handleNotificationResponse,
  type IntentContext,
  type IntentResult,
} from '@/features/notifications/notificationIntentQueue';
import { sweepSelfieCache } from '@/features/attendance/selfieFiles';

function notificationContext(): IntentContext {
  const { isAuthenticated, user } = useAuthStore.getState();
  return {
    user: isAuthenticated ? user : null,
    navigationReady: navigationRef.isReady(),
    navigate: (name, params) => navigationRef.navigate(name, params),
  };
}

function showNotificationResult(result: IntentResult | { status: 'duplicate' }) {
  if (result.status === 'unavailable') {
    Alert.alert(i18n.t('notifications.unavailable'), i18n.t(result.messageKey));
  }
}

function consumeNotificationIntent() {
  showNotificationResult(consumeReadyIntent(notificationContext()));
}

function openFromNotification(response: Parameters<typeof handleNotificationResponse>[0]) {
  showNotificationResult(handleNotificationResponse(response, notificationContext));
}

function AppContent() {
  const { theme } = useTheme();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const user = useAuthStore((state) => state.user);
  useSnapshotProtection(isAuthenticated);

  // After the first screen is up: offer the restart if the saved language and the layout direction
  // disagree. Raised any earlier, Android can drop the alert (config/i18n.ts).
  useEffect(() => {
    const task = InteractionManager.runAfterInteractions(() => {
      promptRestartIfNeeded().catch((error) => {
        if (__DEV__) console.warn('[i18n] Restart prompt failed:', error);
      });
    });
    return () => task.cancel();
  }, []);

  // Selfie photos never stay on the phone: delete any left behind by a crash or a kill mid-capture.
  useEffect(() => {
    void sweepSelfieCache();
  }, []);

  useEffect(() => {
    let active = true;
    let cleanup: () => void = () => {};
    setupNotificationListeners(
      (notification) => {
        if (__DEV__) console.log('[Notification] Received:', notification.request.content.title);
      },
      (response) => {
        openFromNotification(response);
      },
    )
      .then((listenerCleanup) => {
        if (active) cleanup = listenerCleanup;
        else listenerCleanup();
      })
      .catch((error) => {
        if (__DEV__) console.warn('[Notification] Listener setup failed:', error);
      });

    getLastNotificationResponseAsync()
      .then((response) => {
        if (response) {
          openFromNotification(response);
        }
      })
      .catch(() => undefined);

    return () => {
      active = false;
      cleanup();
    };
  }, []);

  useEffect(() => {
    consumeNotificationIntent();
  }, [isAuthenticated, user]);

  useEffect(() => navigationRef.addListener('state', consumeNotificationIntent), []);

  return (
    <>
      <StatusBar style={theme.isDark ? 'light' : 'dark'} />
      <ErrorBoundary ref={TOUR_MODE ? (boundary) => registerBoundaryReset(() => boundary?.reset()) : undefined}>
        <RootNavigator />
        {TOUR_MODE ? <ScreenTour /> : null}
      </ErrorBoundary>
    </>
  );
}
export default function App() {
  return (
    <SafeAreaProvider>
      <ThemeProvider>
        <AppContent />
      </ThemeProvider>
    </SafeAreaProvider>
  );
}
