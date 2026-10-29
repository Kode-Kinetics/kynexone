// ============================================================
// KynexOne Mobile — App root
// ============================================================

import { promptRestartIfNeeded } from '@/config/i18n'; // Initialize i18n before anything renders
import React, { useEffect } from 'react';
import { InteractionManager } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { RootNavigator } from '@/navigation/RootNavigator';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { ScreenTour, TOUR_MODE, registerBoundaryReset } from '@/dev/ScreenTour';
import { useAuthStore } from '@/auth/authStore';
import { navigateFromRoot } from '@/navigation/routes';
import { ThemeProvider, useTheme } from '@/theme/ThemeProvider';
import {
  setupNotificationListeners,
  getLastNotificationResponseAsync,
  getNotificationRoute,
} from '@/features/notifications/pushNotifications';
import { OfflineBanner } from '@/features/safety/OfflineBanner';
import { AppLockGate } from '@/features/safety/AppLockGate';
import { noteLaunchFailure, startLaunchHealthCheck } from '@/updates/startupHealth';
import { initErrorReporting } from '@/observability/initErrorReporting';
import { reportError } from '@/observability/reportError';
import { sweepSelfieCache } from '@/features/attendance/selfieFiles';

initErrorReporting();

function openFromNotification(data: Record<string, unknown> | undefined) {
  const { route, params } = getNotificationRoute(data);
  const user = useAuthStore.getState().user;
  if (!user) return;
  let attempts = 0;
  const tryNavigate = () => {
    if (navigateFromRoot(route, user, params)) return;
    if (++attempts < 20) setTimeout(tryNavigate, 250);
  };
  tryNavigate();
}

function AppContent() {
  const { theme } = useTheme();

  // After the first screen is up: offer the restart if the saved language and the layout direction
  // disagree. Raised any earlier, Android can drop the alert (config/i18n.ts).
  useEffect(() => {
    const task = InteractionManager.runAfterInteractions(() => {
      promptRestartIfNeeded().catch((error) => console.warn('[i18n] Restart prompt failed:', error));
    });
    return () => task.cancel();
  }, []);

  // OTA safety: an update that never gets healthy is replaced by what the server serves now.
  useEffect(() => {
    void startLaunchHealthCheck().catch((error) => console.warn('[updates] Health check failed:', error?.message));
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
        // Titles can carry names and amounts: nothing about the notification is logged.
        void notification;
      },
      (response) => {
        openFromNotification(response.notification.request.content.data as Record<string, unknown>);
      },
    )
      .then((listenerCleanup) => {
        if (active) cleanup = listenerCleanup;
        else listenerCleanup();
      })
      .catch((error) => console.warn('[Notification] Listener setup failed:', error));

    getLastNotificationResponseAsync()
      .then((response) => {
        if (response) {
          openFromNotification(response.notification.request.content.data as Record<string, unknown>);
        }
      })
      .catch(() => undefined);

    return () => {
      active = false;
      cleanup();
    };
  }, []);

  return (
    <>
      <StatusBar style={theme.isDark ? 'light' : 'dark'} />
      <ErrorBoundary
        onError={(error, componentStack) => {
          noteLaunchFailure();
          reportError(error, { source: 'boundary', tags: { stackDepth: componentStack?.split('\n').length } });
        }}
        ref={TOUR_MODE ? (boundary) => registerBoundaryReset(() => boundary?.reset()) : undefined}>
        <RootNavigator />
        {TOUR_MODE ? <ScreenTour /> : null}
        <OfflineBanner />
        <AppLockGate />
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
