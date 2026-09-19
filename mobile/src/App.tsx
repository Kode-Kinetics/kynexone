// ============================================================
// KynexOne Mobile — App root
// ============================================================

import '@/config/i18n'; // Initialize i18n before anything renders
import React, { useEffect } from 'react';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { RootNavigator } from '@/navigation/RootNavigator';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { ScreenTour, TOUR_MODE, registerBoundaryReset } from '@/dev/ScreenTour';
import { useAuthStore } from '@/auth/authStore';
import { isManagerUser, navigateFromRoot } from '@/navigation/routes';
import {
  setupNotificationListeners,
  getLastNotificationResponseAsync,
  getNotificationRoute,
} from '@/features/notifications/pushNotifications';

function openFromNotification(data: Record<string, unknown> | undefined) {
  const { route, params } = getNotificationRoute(data);
  const user = useAuthStore.getState().user;
  if (!user) return; // signed out: the login screen is the right place to land
  // The navigator may still be mounting on a cold start — retry briefly.
  let attempts = 0;
  const tryNavigate = () => {
    if (navigateFromRoot(route, isManagerUser(user), params)) return;
    if (++attempts < 20) setTimeout(tryNavigate, 250);
  };
  tryNavigate();
}

export default function App() {
  useEffect(() => {
    let active = true;
    let cleanup: () => void = () => {};

    setupNotificationListeners(
      (notification) => {
        console.log('[Notification] Received:', notification.request.content.title);
      },
      (response) => {
        openFromNotification(response.notification.request.content.data as Record<string, unknown>);
      }
    )
      .then((listenerCleanup) => {
        if (active) cleanup = listenerCleanup;
        else listenerCleanup();
      })
      .catch((error) => console.warn('[Notification] Listener setup failed:', error));

    getLastNotificationResponseAsync()
      .then((response) => {
        if (response) openFromNotification(response.notification.request.content.data as Record<string, unknown>);
      })
      .catch(() => undefined);

    return () => {
      active = false;
      cleanup();
    };
  }, []);

  return (
    <SafeAreaProvider>
      <StatusBar style="light" />
      <ErrorBoundary ref={TOUR_MODE ? (b) => registerBoundaryReset(() => b?.reset()) : undefined}>
        <RootNavigator />
        {TOUR_MODE ? <ScreenTour /> : null}
      </ErrorBoundary>
    </SafeAreaProvider>
  );
}
