import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { pathToFileURL } from 'node:url';
import type { AccessMode, AuthUser } from '../src/types/index.ts';

function moduleUrl(source: string): string {
  return `data:text/javascript,${encodeURIComponent(source)}`;
}

const sourceRoot = pathToFileURL(`${process.cwd()}/src/`);
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === '@react-navigation/native') {
      return {
        url: moduleUrl(`export function createNavigationContainerRef() {
          return { isReady: () => false, navigate() {} };
        }`),
        shortCircuit: true,
      };
    }
    if (specifier.startsWith('@/')) {
      return nextResolve(new URL(`${specifier.slice(2)}.ts`, sourceRoot).href, context);
    }
    if (specifier.startsWith('.') && !specifier.endsWith('.ts') && context.parentURL?.startsWith(sourceRoot.href)) {
      return nextResolve(`${specifier}.ts`, context);
    }
    return nextResolve(specifier, context);
  },
});

const notificationIntentModule = await import('../src/features/notifications/notificationIntentQueue.ts');
const {
  createNotificationIntentQueue,
  handleNotificationTap,
  parseNotificationIntent,
} = notificationIntentModule;

function user(mode: AccessMode = 'ESSOnly', permissions: string[] = ['ess.read']): AuthUser {
  const grouped = new Map<string, string[]>();
  for (const permission of permissions) {
    const [module, action] = permission.split('.');
    grouped.set(module, [...(grouped.get(module) ?? []), action]);
  }
  return {
    id: 'user-1',
    tenantId: 'tenant-1',
    employeeId: 'employee-1',
    username: 'employee@example.com',
    email: 'employee@example.com',
    fullName: 'Example Employee',
    role: 'EMPLOYEE',
    accessMode: mode,
    permissions: [...grouped].map(([module, actions]) => ({ module, actions })),
    isFirstLogin: false,
    isActive: true,
    mustChangePassword: false,
  };
}

function navigationHarness() {
  const calls: { name: string; params?: object }[] = [];
  return {
    calls,
    navigate: (name: string, params?: object) => calls.push({ name, params }),
  };
}

test('an intent delivered before authentication waits and navigates after sign-in', () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  queue.enqueueIntent({ route: 'PayslipDetail', params: { id: 'pay-7' } });

  assert.deepEqual(
    queue.consumeReadyIntent({ user: null, navigationReady: true, navigate: navigation.navigate }),
    { status: 'waiting' },
  );
  assert.equal(navigation.calls.length, 0);

  const result = queue.consumeReadyIntent({ user: user(), navigationReady: true, navigate: navigation.navigate });
  assert.equal(result.status, 'navigated');
  assert.deepEqual(navigation.calls, [{
    name: 'Payslips',
    params: { screen: 'PayslipDetail', params: { id: 'pay-7' }, initial: false },
  }]);
});

test('an intent delivered before navigation is ready remains pending', () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  queue.enqueueIntent({ route: 'HRRequests' });

  assert.deepEqual(
    queue.consumeReadyIntent({ user: user(), navigationReady: false, navigate: navigation.navigate }),
    { status: 'waiting' },
  );
  assert.equal(navigation.calls.length, 0);

  assert.equal(
    queue.consumeReadyIntent({ user: user(), navigationReady: true, navigate: navigation.navigate }).status,
    'navigated',
  );
  assert.equal(navigation.calls.length, 1);
});

test('a ready intent is consumed exactly once', () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  queue.enqueueIntent({ route: 'Notifications' });
  const context = { user: user(), navigationReady: true, navigate: navigation.navigate };

  assert.equal(queue.consumeReadyIntent(context).status, 'navigated');
  assert.deepEqual(queue.consumeReadyIntent(context), { status: 'empty' });
  assert.equal(navigation.calls.length, 1);
});

test('payload parsing accepts allowlisted routes and gives malformed payloads a contextual fallback', () => {
  assert.deepEqual(
    parseNotificationIntent({ actionRoute: 'PayslipDetail', actionId: 'pay-7' }),
    { route: 'PayslipDetail', params: { id: 'pay-7' } },
  );

  const malformed = parseNotificationIntent({ actionRoute: 'AdminSecrets', actionId: 'secret-1' });
  assert.equal(malformed.route, 'Notifications');
  assert.equal(malformed.unavailableMessageKey, 'notifications.destinationUnavailable');
});

test('approval notifications carry only a validated approval request identifier', () => {
  const approvalRequestId = '5c692beb-bbb4-4505-a790-6fcceee11c80';
  assert.deepEqual(
    parseNotificationIntent({ actionRoute: 'Approvals', approvalRequestId }),
    { route: 'Approvals', params: { taskId: approvalRequestId } },
  );
  assert.deepEqual(
    parseNotificationIntent({ type: 'APPROVAL_REQUIRED', entityName: 'ApprovalRequest', entityId: approvalRequestId }),
    { route: 'Approvals', params: { taskId: approvalRequestId } },
  );
  assert.deepEqual(
    parseNotificationIntent({ actionRoute: 'Approvals', actionId: approvalRequestId, entityId: approvalRequestId }),
    { route: 'Approvals' },
  );
  assert.deepEqual(
    parseNotificationIntent({ type: 'APPROVAL_REQUIRED', entityName: 'LeaveRequest', entityId: approvalRequestId }),
    { route: 'Approvals' },
  );
  assert.deepEqual(
    parseNotificationIntent({ actionRoute: 'Approvals', approvalRequestId: 'not-a-guid' }),
    { route: 'Approvals' },
  );
});

test('a denied target opens Notifications with context and never silently redirects to Account', () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  queue.enqueueIntent({ route: 'Approvals' });

  const result = queue.consumeReadyIntent({
    user: user('ESSOnly', ['ess.read']),
    navigationReady: true,
    navigate: navigation.navigate,
  });

  assert.equal(result.status, 'unavailable');
  assert.equal(result.status === 'unavailable' ? result.messageKey : '', 'notifications.destinationAccessDenied');
  assert.deepEqual(navigation.calls, [{ name: 'More', params: { screen: 'Notifications', params: undefined, initial: false } }]);
  assert.equal(navigation.calls.some((call) => call.name === 'Account'), false);
});

test('tapping a notification row marks it read and navigates to its destination', async () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  const markedRead: string[] = [];

  const result = await handleNotificationTap(
    {
      id: 'notification-1',
      type: 'PAYSLIP_PUBLISHED',
      actionRoute: 'PayslipDetail',
      actionId: 'pay-7',
    },
    async (id) => { markedRead.push(id); },
    () => ({ user: user(), navigationReady: true, navigate: navigation.navigate }),
    queue,
  );

  assert.deepEqual(markedRead, ['notification-1']);
  assert.equal(result.status, 'navigated');
  assert.deepEqual(navigation.calls, [{
    name: 'Payslips',
    params: { screen: 'PayslipDetail', params: { id: 'pay-7' }, initial: false },
  }]);
});

test('the same Expo response from the live listener and cold-start replay navigates once', () => {
  const handleNotificationResponse = (
    notificationIntentModule as Record<string, unknown>
  ).handleNotificationResponse as undefined | ((
    response: object,
    getContext: () => { user: AuthUser; navigationReady: boolean; navigate: (name: string, params?: object) => void },
    queue: ReturnType<typeof createNotificationIntentQueue>,
  ) => { status: string });
  assert.equal(typeof handleNotificationResponse, 'function');

  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  const response = {
    actionIdentifier: 'expo.modules.notifications.actions.DEFAULT',
    notification: {
      request: {
        identifier: 'expo-request-42',
        content: {
          data: {
            actionRoute: 'PayslipDetail',
            actionId: 'pay-7',
          },
        },
      },
    },
  };
  const getContext = () => ({
    user: user(),
    navigationReady: true,
    navigate: navigation.navigate,
  });

  assert.equal(handleNotificationResponse?.(response, getContext, queue).status, 'navigated');
  assert.equal(handleNotificationResponse?.(response, getContext, queue).status, 'duplicate');
  assert.equal(navigation.calls.length, 1);
});

test('a delayed row read receipt consumes with current permissions rather than captured permissions', async () => {
  const queue = createNotificationIntentQueue();
  const navigation = navigationHarness();
  let context = {
    user: user('ESSOnly', ['ess.read']),
    navigationReady: true,
    navigate: navigation.navigate,
  };
  const handleWithLiveContext = handleNotificationTap as unknown as (
    notification: { id: string; actionRoute: string },
    markRead: (id: string) => Promise<void>,
    getContext: () => typeof context,
    intentQueue: ReturnType<typeof createNotificationIntentQueue>,
  ) => Promise<{ status: string }>;

  const result = await handleWithLiveContext(
    { id: 'notification-2', actionRoute: 'Approvals' },
    async () => {
      context = {
        user: user('ESSOnly', []),
        navigationReady: true,
        navigate: navigation.navigate,
      };
    },
    () => context,
    queue,
  );

  assert.equal(result.status, 'unavailable');
  assert.equal(navigation.calls.some((call) => call.name === 'Approvals'), false);
  assert.equal(navigation.calls.some((call) => call.name === 'Account'), false);
  assert.deepEqual(queue.consumeReadyIntent(context), { status: 'empty' });
});
