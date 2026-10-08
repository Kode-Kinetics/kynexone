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
import { sweepSelfieCache } from '@/features/attendance/selfieFiles';

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

  // Selfie photos never stay on the phone: delete any left behind by a crash or a kill mid-capture.
  useEffect(() => {
    void sweepSelfieCache();
  }, []);

  useEffect(() => {
    let active = true;
    let cleanup: () => void = () => {};
    setupNotificationListeners(
      (notification) => {
        console.log('[Notification] Received:', notification.request.content.title);
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
