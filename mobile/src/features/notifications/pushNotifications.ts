import type { Notification, NotificationResponse } from 'expo-notifications';
import * as Device from 'expo-device';
import { Platform } from 'react-native';
import Constants from 'expo-constants';
import { deviceApi } from '@/api/services';
import type { AppRoute } from '@/navigation/routes';

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
    console.log('[Push] Simulator detected – push registration is disabled');
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
    console.warn('[Push] Permission not granted');
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
    console.warn('[Push] No EAS projectId configured (EXPO_PUBLIC_EAS_PROJECT_ID) – skipping push registration');
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

    console.log('[Push] Registered push token:', token.slice(0, 20) + '...');
    return token;
  } catch (error) {
    console.error('[Push] Failed to register push token:', error);
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
    console.warn('[Push] Failed to unregister push token:', error);
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
  const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : undefined);
  const type = str(data?.type) ?? str(data?.eventCode) ?? '';
  const entityName = (str(data?.entityName) ?? '').toLowerCase();
  const entityId = str(data?.entityId);
  const key = `${type} ${entityName}`.toLowerCase();

  if (/payslip|payroll\.run/.test(key)) {
    const id = str(data?.payslipId) ?? (entityName.includes('payslip') ? entityId : undefined);
    return id ? { route: 'PayslipDetail', params: { id } } : { route: 'Payslips' };
  }
  if (/hr_?request/.test(key)) {
    const id = str(data?.requestId) ?? (entityName.includes('hrrequest') ? entityId : undefined);
    return id ? { route: 'HRRequestDetail', params: { id } } : { route: 'HRRequests' };
  }
  if (/approvalpending|approval_?request|approvalrequest/.test(key)) return { route: 'Approvals' };
  if (/overtime/.test(key)) return { route: 'Overtime' };
  if (/leave/.test(key)) return { route: 'ApplyLeave' };
  if (/document|compliance_document_expiry/.test(key)) return { route: 'Documents' };
  if (/missingpunch|attendancealert|regularization|attendance/.test(key)) {
    const date = str(data?.date);
    return { route: 'AttendanceCorrection', params: date ? { date } : undefined };
  }
  return { route: 'Notifications' };
}
