import { canNavigateTo, resolveRoute, type AppRoute } from '@/navigation/routes';
import type { AuthUser } from '@/types';

type UnavailableMessageKey = 'notifications.destinationUnavailable'
  | 'notifications.destinationAccessDenied'
  | 'notifications.destinationWorkspaceDenied';

export interface NotificationIntent {
  route: AppRoute;
  params?: Record<string, unknown>;
  unavailableMessageKey?: UnavailableMessageKey;
  responseIdentity?: string;
}

export interface IntentContext {
  user: AuthUser | null | undefined;
  navigationReady: boolean;
  navigate: (name: string, params?: object) => void;
}

export type IntentResult =
  | { status: 'empty' }
  | { status: 'waiting' }
  | { status: 'navigated' }
  | { status: 'unavailable'; messageKey: UnavailableMessageKey };

export interface NotificationIntentQueue {
  enqueueIntent: (intent: NotificationIntent) => boolean;
  consumeReadyIntent: (context: IntentContext) => IntentResult;
}

export interface NotificationResponseLike {
  actionIdentifier?: unknown;
  notification?: {
    request?: {
      identifier?: unknown;
      content?: { data?: unknown };
    };
  };
}

export type NotificationResponseResult = IntentResult | { status: 'duplicate' };

export interface NotificationActionPayload {
  id?: string;
  type?: unknown;
  eventCode?: unknown;
  entityName?: unknown;
  entityId?: unknown;
  actionRoute?: unknown;
  actionId?: unknown;
  route?: unknown;
  payslipId?: unknown;
  requestId?: unknown;
  approvalRequestId?: unknown;
  date?: unknown;
}

const EXPLICIT_ROUTES: Readonly<Record<string, AppRoute>> = {
  notifications: 'Notifications',
  payslips: 'Payslips',
  payslipdetail: 'PayslipDetail',
  hrrequests: 'HRRequests',
  hrrequestdetail: 'HRRequestDetail',
  approvals: 'Approvals',
  overtime: 'Overtime',
  applyleave: 'ApplyLeave',
  documents: 'Documents',
  attendancehistory: 'AttendanceHistory',
  attendancecorrection: 'AttendanceCorrection',
};

function stringValue(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
}

function recordValue(value: unknown): NotificationActionPayload | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as NotificationActionPayload
    : undefined;
}

function explicitRoute(value: unknown): AppRoute | undefined {
  const route = stringValue(value);
  if (!route) return undefined;
  return EXPLICIT_ROUTES[route.replace(/^\/+|\/+$/g, '').replace(/[-_\s]/g, '').toLowerCase()];
}

function approvalRequestIdentifier(value: unknown): string | undefined {
  const identifier = stringValue(value);
  if (!identifier) return undefined;
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(identifier)
    ? identifier
    : undefined;
}

function detailParams(route: AppRoute, payload: NotificationActionPayload): Record<string, unknown> | undefined {
  const actionId = stringValue(payload.actionId) ?? stringValue(payload.entityId);
  if (route === 'PayslipDetail') {
    const id = stringValue(payload.payslipId) ?? actionId;
    return id ? { id } : undefined;
  }
  if (route === 'HRRequestDetail') {
    const id = stringValue(payload.requestId) ?? actionId;
    return id ? { id } : undefined;
  }
  if (route === 'Approvals') {
    const taskId = approvalRequestIdentifier(payload.approvalRequestId);
    return taskId ? { taskId } : undefined;
  }
  if (route === 'AttendanceCorrection') {
    const date = stringValue(payload.date);
    return date ? { date } : undefined;
  }
  return undefined;
}

export function parseNotificationIntent(
  payload: NotificationActionPayload | null | undefined,
): NotificationIntent {
  const data = payload ?? {};
  const requestedRoute = explicitRoute(data.actionRoute) ?? explicitRoute(data.route);
  if (requestedRoute) {
    const params = detailParams(requestedRoute, data);
    if (requestedRoute === 'PayslipDetail' && !params) return { route: 'Payslips' };
    if (requestedRoute === 'HRRequestDetail' && !params) return { route: 'HRRequests' };
    return params ? { route: requestedRoute, params } : { route: requestedRoute };
  }

  const type = stringValue(data.type) ?? stringValue(data.eventCode) ?? '';
  const entityName = (stringValue(data.entityName) ?? '').toLowerCase();
  const entityId = stringValue(data.entityId);
  const key = `${type} ${entityName}`.toLowerCase();

  if (/payslip|payroll\.run/.test(key)) {
    const id = stringValue(data.payslipId) ?? (entityName.includes('payslip') ? entityId : undefined);
    return id ? { route: 'PayslipDetail', params: { id } } : { route: 'Payslips' };
  }
  if (/hr_?request/.test(key)) {
    const id = stringValue(data.requestId) ?? (entityName.includes('hrrequest') ? entityId : undefined);
    return id ? { route: 'HRRequestDetail', params: { id } } : { route: 'HRRequests' };
  }
  if (/approval_?(required|pending|request)|approvalrequest/.test(key)) {
    const taskId = approvalRequestIdentifier(data.approvalRequestId)
      ?? (entityName.replace(/[\s_-]/g, '') === 'approvalrequest'
        ? approvalRequestIdentifier(data.entityId)
        : undefined);
    return taskId ? { route: 'Approvals', params: { taskId } } : { route: 'Approvals' };
  }
  if (/overtime/.test(key)) return { route: 'Overtime' };
  if (/leave/.test(key)) return { route: 'ApplyLeave' };
  if (/document|compliance_document_expiry|policy_acknowledgment/.test(key)) return { route: 'Documents' };
  if (/missing_?punch|attendance_?alert|regularization|attendance/.test(key)) {
    const date = stringValue(data.date);
    return { route: 'AttendanceCorrection', params: date ? { date } : undefined };
  }
  return { route: 'Notifications', unavailableMessageKey: 'notifications.destinationUnavailable' };
}

export function createNotificationIntentQueue(): NotificationIntentQueue {
  let pending: NotificationIntent | undefined;
  const handledResponseIdentities = new Set<string>();
  const handledResponseOrder: string[] = [];

  return {
    enqueueIntent(intent) {
      const identity = intent.responseIdentity;
      if (identity && handledResponseIdentities.has(identity)) return false;
      if (identity) {
        handledResponseIdentities.add(identity);
        handledResponseOrder.push(identity);
        if (handledResponseOrder.length > 32) {
          const oldestIdentity = handledResponseOrder.shift();
          if (oldestIdentity) handledResponseIdentities.delete(oldestIdentity);
        }
      }
      pending = intent;
      return true;
    },

    consumeReadyIntent(context) {
      if (!pending) return { status: 'empty' };
      if (!context.user || !context.navigationReady) return { status: 'waiting' };

      const intent = pending;
      pending = undefined;
      if (canNavigateTo(intent.route, context.user)) {
        const resolution = resolveRoute(intent.route, intent.params, context.user);
        context.navigate(resolution.name, resolution.params);
        return intent.unavailableMessageKey
          ? { status: 'unavailable', messageKey: intent.unavailableMessageKey }
          : { status: 'navigated' };
      }

      const fallbackAllowed = canNavigateTo('Notifications', context.user);
      const fallback = resolveRoute('Notifications', undefined, context.user);
      if (fallbackAllowed) context.navigate(fallback.name, fallback.params);
      return {
        status: 'unavailable',
        messageKey: fallbackAllowed
          ? 'notifications.destinationAccessDenied'
          : 'notifications.destinationWorkspaceDenied',
      };
    },
  };
}

const sharedQueue = createNotificationIntentQueue();

export const enqueueIntent = sharedQueue.enqueueIntent;
export const consumeReadyIntent = sharedQueue.consumeReadyIntent;

function notificationResponseIdentity(response: NotificationResponseLike): string | undefined {
  const requestIdentifier = stringValue(response.notification?.request?.identifier);
  if (!requestIdentifier) return undefined;
  const actionIdentifier = stringValue(response.actionIdentifier) ?? 'default';
  return `${requestIdentifier}\u001F${actionIdentifier}`;
}

export function handleNotificationResponse(
  response: NotificationResponseLike,
  getContext: () => IntentContext,
  queue: NotificationIntentQueue = sharedQueue,
): NotificationResponseResult {
  const intent = parseNotificationIntent(recordValue(response.notification?.request?.content?.data));
  const responseIdentity = notificationResponseIdentity(response);
  if (!queue.enqueueIntent(responseIdentity ? { ...intent, responseIdentity } : intent)) {
    return { status: 'duplicate' };
  }
  return queue.consumeReadyIntent(getContext());
}

export async function handleNotificationTap(
  notification: NotificationActionPayload & { id: string },
  markRead: (id: string) => Promise<void>,
  getContext: () => IntentContext,
  queue: NotificationIntentQueue = sharedQueue,
): Promise<IntentResult> {
  queue.enqueueIntent(parseNotificationIntent(notification));
  try {
    await markRead(notification.id);
  } catch {
    // Reading the destination is still useful if the read receipt cannot sync.
  }
  return queue.consumeReadyIntent(getContext());
}
