import type { Notification, NotificationResponse } from 'expo-notifications';
import * as Device from 'expo-device';
import { Platform } from 'react-native';
import Constants from 'expo-constants';
import { deviceApi } from '@/api/services';
import type { AppRoute } from '@/navigation/routes';
import { parseNotificationIntent } from './notificationIntentQueue';

type NotificationsModule = typeof import('expo-notifications');
let notificationsModulePromise: Promise<NotificationsModule> | null = null;
let notificationHandlerConfigured = false;

async function loadNotifications(): Promise<NotificationsModule> {
  notificationsModulePromise ??= import('expo-notifications');
  return notificationsModulePromise;
}

function configureForegroundHandler(Notifications: NotificationsModule) {
  if (notificationHandlerConfigured) return;
  Notifications.setNotificationHandler({
    handleNotification: async () => ({
      shouldShowAlert: true,
      shouldShowBanner: true,
      shouldShowList: true,
      shouldPlaySound: true,
      shouldSetBadge: true,
    }),
  });
  notificationHandlerConfigured = true;
}

/**
 * Request push notification permissions and register the token with the backend.
 * Call this after the user is authenticated.
 */
export async function registerPushToken(deviceId: string): Promise<string | null> {
  if (!Device.isDevice) {
    // Loading expo-notifications in an unsigned simulator build can emit a
    // keychain-registration error. Skip the native module entirely there.
    if (__DEV__) console.log('[Push] Simulator detected – push registration is disabled');
    return null;
  }

  const Notifications = await loadNotifications();
  configureForegroundHandler(Notifications);

  // Request permission
  const { status: existingStatus } = await Notifications.getPermissionsAsync();
  let finalStatus = existingStatus;
  if (existingStatus !== 'granted') {
    const { status } = await Notifications.requestPermissionsAsync();
    finalStatus = status;
  }
  if (finalStatus !== 'granted') {
    if (__DEV__) console.warn('[Push] Permission not granted');
    return null;
  }

  // Android notification channel
  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync('default', {
      name: 'KynexOne Notifications',
      importance: Notifications.AndroidImportance.MAX,
      vibrationPattern: [0, 250, 250, 250],
      lightColor: '#2F6BFF',
    });
    await Notifications.setNotificationChannelAsync('approvals', {
      name: 'Approvals',
      importance: Notifications.AndroidImportance.HIGH,
      vibrationPattern: [0, 250, 250, 250],
      lightColor: '#2F6BFF',
    });
  }

  // Expo push tokens are scoped to an EAS project. Until the EAS project exists
  // (deferred to the store-submission phase) there is no id to ask for, so skip
  // registration instead of throwing on every sign-in.
  const projectId = (Constants.expoConfig?.extra as { eas?: { projectId?: string } } | undefined)?.eas
    ?.projectId;
  if (!projectId) {
    if (__DEV__) console.warn('[Push] No EAS projectId configured (EXPO_PUBLIC_EAS_PROJECT_ID) – skipping push registration');
    return null;
  }

  // Get token
  try {
    const tokenData = await Notifications.getExpoPushTokenAsync({ projectId });
    const token = tokenData.data;

    // Register with backend
    await deviceApi.register({
      deviceId,
      pushToken: token,
      platform: Platform.OS as 'ios' | 'android',
      model: Device.modelName ?? 'unknown',
      osVersion: Device.osVersion ?? 'unknown',
      appVersion: Constants.expoConfig?.version ?? '1.0.0',
    });

    if (__DEV__) console.log('[Push] Registered push token:', token.slice(0, 20) + '...');
    return token;
  } catch (error) {
    if (__DEV__) console.error('[Push] Failed to register push token:', error);
    return null;
  }
}

/**
 * Unregister push token on logout
 */
export async function unregisterPushToken(deviceId: string): Promise<void> {
  try {
    await deviceApi.unregister(deviceId);
  } catch (error) {
    if (__DEV__) console.warn('[Push] Failed to unregister push token:', error);
  }
}

/**
 * Set up notification response listener (tap on notification)
 */
export async function setupNotificationListeners(
  onNotification: (notification: Notification) => void,
  onResponse: (response: NotificationResponse) => void
): Promise<() => void> {
  if (!Device.isDevice) return () => undefined;
  const Notifications = await loadNotifications();
  configureForegroundHandler(Notifications);
  const notifSub = Notifications.addNotificationReceivedListener(onNotification);
  const responseSub = Notifications.addNotificationResponseReceivedListener(onResponse);

  return () => {
    notifSub.remove();
    responseSub.remove();
  };
}

export async function getLastNotificationResponseAsync(): Promise<NotificationResponse | null> {
  if (!Device.isDevice) return null;
  const Notifications = await loadNotifications();
  return Notifications.getLastNotificationResponseAsync();
}

/**
 * Resolve a tapped push notification to an in-app destination.
 *
 * Today the backend's Expo payload carries only `{ idempotencyKey }` — no event
 * code and no entity reference — so every push resolves to the Notifications
 * list, which is a correct, non-broken landing page. The mapping below is ready
 * for the payload the backend should send (see the Wave-2 spec: `type` = the
 * notification EventCode, plus `entityName` / `entityId`), and also accepts the
 * legacy app-side type names. Anything unrecognised degrades to Notifications.
 */
export function getNotificationRoute(
  data: Record<string, unknown> | null | undefined
): { route: AppRoute; params?: Record<string, unknown> } {
  return parseNotificationIntent(data);
}
