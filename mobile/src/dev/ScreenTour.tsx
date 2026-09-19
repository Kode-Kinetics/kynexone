// ============================================================
// KynexOne Mobile — Dev-only screen tour (QA harness)
// ============================================================
//
// Drives every screen of the running app against a live backend, without taps,
// so a simulator run can be screenshotted and its HTTP traffic read from the
// Metro log. Inert unless EXPO_PUBLIC_SCREEN_TOUR is set when the bundle is
// built (EXPO_PUBLIC_* is inlined at bundle time; never set it for EAS/store
// builds). It deliberately also works with `expo start --no-dev`, because only a
// production-mode bundle shows the ErrorBoundary fallback exactly as users see
// it — in dev, React's red-box overlays it.
//
//   EXPO_PUBLIC_SCREEN_TOUR=employee|manager
//   EXPO_PUBLIC_TOUR_TENANT=…  EXPO_PUBLIC_TOUR_EMAIL=…  EXPO_PUBLIC_TOUR_PASSWORD=…
//
// Each step logs "[TOUR] STEP <name>" when its screen is on display and
// "[TOUR] RESULT <name> <detail>" for the actions it performs through the same
// service functions the screens call. The final step throws during render to
// prove the root ErrorBoundary, then resets it.

import { useEffect, useState } from 'react';
import { LogBox } from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import { useAuthStore } from '@/auth/authStore';
import { navigateFromRoot, navigationRef, isManagerUser, type AppRoute } from '@/navigation/routes';
import {
  attendanceApi,
  dashboardApi,
  leaveApi,
  overtimeApi,
  payslipApi,
  profileApi,
  documentsApi,
  hrRequestsApi,
  notificationsApi,
  aiApi,
  approvalsApi,
  teamApi,
  authApi,
  type PickedFile,
} from '@/api/services';

export const TOUR_MODE: string | undefined = process.env.EXPO_PUBLIC_SCREEN_TOUR || undefined;

// The dev LogBox red-box would cover the ErrorBoundary fallback in screenshots;
// hide it so the tour shows exactly what a release build shows.
if (TOUR_MODE) LogBox.ignoreAllLogs(true);

const DELIBERATE = 'Deliberate render failure injected by the dev screen tour';

// The tour intentionally relies on the normal React error boundary. Avoid
// private React Native deep imports so SDK upgrades remain warning-free.

const DWELL_MS = 9000;
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
const log = (...args: unknown[]) => console.log('[TOUR]', ...args);

let resetBoundary: (() => void) | null = null;
// The boundary remounts children on reset and Fast Refresh re-executes this
// module — keep the once-only flag on the global object so neither restarts it.
const g = globalThis as unknown as { __kxTourStarted?: boolean };
export function registerBoundaryReset(fn: () => void) {
  resetBoundary = fn;
}

async function result(name: string, fn: () => Promise<unknown>) {
  try {
    const value = await fn();
    log('RESULT', name, 'OK', typeof value === 'string' ? value : JSON.stringify(value)?.slice(0, 300));
  } catch (e: any) {
    log('RESULT', name, 'FAIL', e?.response?.status ?? '', e?.message);
  }
}

function addDays(n: number): string {
  const d = new Date();
  d.setDate(d.getDate() + n);
  return d.toISOString().slice(0, 10);
}

const TOUR_PNG_BASE64 =
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII=';

async function createTourUpload(): Promise<PickedFile> {
  const uri = `${FileSystem.cacheDirectory}kynexone-tour-upload.png`;
  await FileSystem.writeAsStringAsync(uri, TOUR_PNG_BASE64, {
    encoding: FileSystem.EncodingType.Base64,
  });
  const info = await FileSystem.getInfoAsync(uri);
  return {
    uri,
    name: 'kynexone-tour-upload.png',
    mimeType: 'image/png',
    size: info.exists ? (info as { size?: number }).size : undefined,
  };
}

async function show(name: string, route: AppRoute, manager: boolean, params?: Record<string, unknown>) {
  navigateFromRoot(route, manager, params);
  log('STEP', name);
  await wait(DWELL_MS);
}

async function employeeTour() {
  const m = false;
  const tourFile = await createTourUpload();
  await show('EmployeeDashboard', 'Home', m);
  await result('punch-in', () => attendanceApi.punch({ punchType: 'CLOCK_IN', timestamp: new Date().toISOString() } as any));
  await result('dashboard-after-in', async () => (await dashboardApi.getEmployeeDashboard()).todayAttendance);
  await result('punch-out', () => attendanceApi.punch({ punchType: 'CLOCK_OUT', timestamp: new Date().toISOString() } as any));
  await result('dashboard-after-out', async () => (await dashboardApi.getEmployeeDashboard()).todayAttendance);
  await show('EmployeeDashboard-after-punch', 'Home', m);

  await show('AttendanceHistory', 'AttendanceHistory', m);
  const corrDate = addDays(-(5 + Math.floor(Math.random() * 20)));
  await show('AttendanceCorrection', 'AttendanceCorrection', m, { date: corrDate });
  await result('regularization', async () => {
    const r = await attendanceApi.submitRegularization({ date: corrDate, requestedClockIn: '09:00', requestedClockOut: '17:30', reason: 'Tour: forgot to punch' });
    return { workDate: r.date, requestedInUtc: r.requestedClockIn, requestedOutUtc: r.requestedClockOut };
  });

  await show('ApplyLeave', 'ApplyLeave', m);
  await result('leave-submit', async () => {
    const types = await leaveApi.getLeaveTypes();
    const annual = types.find((t) => /annual/i.test(t.name)) ?? types[0];
    const d = addDays(40);
    return leaveApi.submitLeaveRequest({ leaveTypeId: annual.id, startDate: d, endDate: d, reason: 'Tour leave' });
  });

  await show('Payslips', 'Payslips', m);
  const slips = await payslipApi.getPayslips().catch(() => []);
  if (slips[0]) {
    await show('PayslipDetail', 'PayslipDetail', m, { id: slips[0].id });
    await result('payslip-detail-lines', async () => {
      const d = await payslipApi.getPayslipDetail(slips[0].id);
      return { earnings: d.earnings.map((e) => `${e.description}=${e.amount}`), deductions: d.deductions.length, net: d.netPay, currency: d.currency };
    });
    await result('payslip-pdf-download', async () => {
      const { url, headers } = await payslipApi.downloadPayslip(slips[0].id);
      const r = await FileSystem.downloadAsync(url, `${FileSystem.cacheDirectory}tour-payslip.pdf`, { headers });
      const info = await FileSystem.getInfoAsync(r.uri);
      return `HTTP ${r.status} bytes=${(info as any).size} type=${r.headers['Content-Type'] ?? r.headers['content-type']}`;
    });
  }

  await show('Profile', 'Profile', m);
  await result('profile-change-request', () => profileApi.requestProfileUpdate({ phone: '+966500000001' }));
  await result('profile-photo-upload', () => profileApi.uploadProfilePhoto(tourFile));

  await show('Documents', 'Documents', m);
  const docs = await documentsApi.getDocuments().catch(() => []);
  log('RESULT', 'documents-list', 'OK', docs.map((d) => `${d.documentType}:${d.fileName}`).join(','));
  if (docs[0]) {
    await result('document-download', async () => {
      const { url, headers } = await documentsApi.downloadDocument(docs[0].id);
      const r = await FileSystem.downloadAsync(url, `${FileSystem.cacheDirectory}tour-doc.pdf`, { headers });
      return `HTTP ${r.status}`;
    });
  }
  await result('document-upload', () =>
    documentsApi.uploadDocument({
      file: tourFile,
      documentType: 'Other',
      documentNumber: `TOUR-${Date.now()}`,
    })
  );

  await show('HRRequests', 'HRRequests', m);
  let hrId: string | undefined;
  await result('hr-request-create', async () => {
    const r = await hrRequestsApi.createRequest({ requestType: 'Salary Certificate', subject: 'Tour: salary certificate', description: 'For bank' });
    hrId = r.id;
    await hrRequestsApi.addComment(r.id, 'Tour comment');
    return r.id;
  });
  if (hrId) {
    await show('HRRequestDetail', 'HRRequestDetail', m, { id: hrId });
    await result('hr-request-detail', async () => {
      const d = await hrRequestsApi.getRequestDetail(hrId!);
      return { status: d.status, comments: d.comments.length, response: d.responseStatus };
    });
  }

  await show('Overtime', 'Overtime', m);
  await result('overtime-submit', () =>
    overtimeApi.submitOTRequest({ date: addDays(-2), startTime: '18:00', endTime: '20:00', reason: 'Tour: release support' })
  );
  await result('overtime-history', async () => (await overtimeApi.getMyOTRequests()).map((r) => `${r.date} ${r.startTime}-${r.endTime} ${r.status}`));

  await show('Notifications', 'Notifications', m);
  await result('notifications', async () => (await notificationsApi.getNotifications()).length);

  await show('AIAssistant', 'AIAssistant', m);
  await result('ai-ask', async () => (await aiApi.ask({ question: 'How many leave days do I have?' })).answer);

  await show('Settings', 'Settings', m);
  await show('ChangePassword', 'ChangePassword' as AppRoute, m);
  await result('change-password-roundtrip', async () => {
    const pw = process.env.EXPO_PUBLIC_TOUR_PASSWORD!;
    await authApi.changePassword(pw, `${pw}x`);
    await authApi.changePassword(`${pw}x`, pw);
    return 'changed and restored';
  });
}

async function managerTour() {
  const m = true;
  await show('ManagerDashboard', 'Home', m);
  await result('manager-dashboard', async () => {
    const d = await dashboardApi.getManagerDashboard();
    return { team: d.teamSummary, pending: d.pendingApprovalsCount, byType: d.pendingByType, ot: d.overtimeAlert };
  });
  await show('Team', 'Team', m);
  await result('team', async () => (await teamApi.getTeamMembers()).map((t) => `${t.fullName}:${t.todayStatus}`));
  await show('Approvals', 'Approvals', m);
  await result('approve-first-pending', async () => {
    const pending = await approvalsApi.getPendingApprovals();
    const target = pending.find((p) => p.canApprove);
    if (!target) return 'no decidable approvals';
    await approvalsApi.approve(target.taskId, 'Approved from tour');
    return `approved ${target.title}`;
  });
  await result('send-back (flagged off)', () => approvalsApi.sendBack('x', 'y'));
  await show('Approvals-after', 'Approvals', m, { filterType: 'ALL' });
  await show('AttendanceHistory-manager', 'AttendanceHistory', m);
  await show('ApplyLeave-manager', 'ApplyLeave', m);
  await show('Payslips-manager', 'Payslips', m);
  await show('Notifications-manager', 'Notifications', m);
}

/** Short run: the two writes whose first attempt collided with earlier data, then the crash. */
async function quickTour() {
  const m = false;
  const otDate = addDays(-(3 + Math.floor(Math.random() * 25)));
  await show('Overtime', 'Overtime', m);
  await result('overtime-submit', async () => {
    const r = await overtimeApi.submitOTRequest({ date: otDate, startTime: '19:00', endTime: '21:00', reason: 'Tour: quick OT' });
    return { date: r.date, start: r.startTime, end: r.endTime, minutes: r.durationMinutes, status: r.status };
  });
  await result('leave-submit', async () => {
    const types = await leaveApi.getLeaveTypes();
    const annual = types.find((t) => /annual/i.test(t.name)) ?? types[0];
    const d = addDays(60 + Math.floor(Math.random() * 60));
    const r = await leaveApi.submitLeaveRequest({ leaveTypeId: annual.id, startDate: d, endDate: d, reason: 'Tour leave' });
    return { start: r.startDate, status: r.status };
  });
}

async function runTour(mode: string) {
  const store = useAuthStore.getState();
  if (!store.isAuthenticated) {
    log('STEP', 'Login');
    await wait(4000);
    await store.login(
      process.env.EXPO_PUBLIC_TOUR_EMAIL!,
      process.env.EXPO_PUBLIC_TOUR_PASSWORD!,
      process.env.EXPO_PUBLIC_TOUR_TENANT!
    );
  }
  await wait(3000);
  const manager = isManagerUser(useAuthStore.getState().user);
  log('RESULT', 'tab-layout', manager ? 'ManagerTabs' : 'EmployeeTabs');
  if (mode === 'manager') await managerTour();
  else if (mode === 'quick') await quickTour();
  else await employeeTour();
}

/** Mount inside the ErrorBoundary. Renders nothing until the crash step. */
export function ScreenTour() {
  const [crash, setCrash] = useState(false);
  useEffect(() => {
    if (!TOUR_MODE || g.__kxTourStarted) return;
    g.__kxTourStarted = true;
    let cancelled = false;
    (async () => {
      await wait(2000);
      while (!navigationRef.isReady() && !cancelled) await wait(250);
      try {
        await runTour(TOUR_MODE);
      } catch (e: any) {
        log('RESULT', 'tour-aborted', 'FAIL', e?.message);
      }
      if (cancelled) return;
      log('STEP', 'ErrorBoundary-crash');
      // Recover after the fallback has been on screen for a dwell.
      setTimeout(() => {
        resetBoundary?.();
        log('STEP', 'ErrorBoundary-recovered');
        setTimeout(async () => {
          if (TOUR_MODE !== 'manager') {
            await useAuthStore.getState().logout();
            await wait(2000);
            navigationRef.navigate('ForgotPassword' as never);
            log('STEP', 'ForgotPassword');
            await result('forgot-password', () =>
              authApi.forgotPassword(process.env.EXPO_PUBLIC_TOUR_EMAIL!, process.env.EXPO_PUBLIC_TOUR_TENANT)
            );
          }
          log('DONE');
        }, DWELL_MS);
      }, DWELL_MS);
      setCrash(true);
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (crash) {
    // Deliberate render-phase exception: the ErrorBoundary must catch this.
    throw new Error(DELIBERATE);
  }
  return null;
}
