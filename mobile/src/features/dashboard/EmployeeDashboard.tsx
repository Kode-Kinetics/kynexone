// Employee Home — one vertical "Today" scroll, top to bottom:
//   1. Time card: today's state, ONE action (Clock in → Clock out → quiet "Day complete").
//   2. KPI tiles that name their period and open their records (annual leave, requests in progress).
//   3. "Needs you": up to three real alerts, each with a verb.
//   4. Labelled quick actions.
//   5. Next public holiday (only when HR loaded a calendar).
//   6. Announcements (only when there are any).
// Every value comes from a persisted record; anything unknown is hidden or shown as unknown.

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Animated, { FadeIn, FadeInDown } from 'react-native-reanimated';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { formatDistanceStrict } from 'date-fns';
import { ar as arLocale, enUS } from 'date-fns/locale';
import { useAuthStore } from '@/auth/authStore';
import { dashboardApi } from '@/api/services';
import { employeeHomeApi, type EmployeeHomeSignals } from '@/api/employeeHome';
import { announcementsApi } from '@/api/announcements';
import type { Announcement, EmployeeHomeExtras, PublicHolidayItem } from '@/api/homeMappers';
import { usePunchFlow, type AttendanceVerificationNotes } from '@/features/attendance/usePunchFlow';
import { navigateTo, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat, type UseFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  BottomSheet,
  DirectionalIcon,
  EmployeeAvatar,
  GlassIconButton,
  KpiTile,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  Skeleton,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import type { EmployeeDashboard, PunchType, TodayAttendance } from '@/types';
import {
  buildNeedsYou,
  classifyDay,
  daysBetween,
  displayFirstName,
  greetingPeriod,
  holidayOn,
  minutesBetween,
  nextHoliday,
  pickAnnualBalance,
  restDayFromHistory,
  splitMinutes,
  summarizePending,
  type DayState,
  type DocumentKind,
  type NeedsItem,
  type PendingPart,
} from './employeeHomeModel';

type IconName = React.ComponentProps<typeof Ionicons>['name'];
type HomeDashboard = EmployeeDashboard & Partial<EmployeeHomeExtras>;

// Registered by lane C (More stack). Until that lands, navigateTo redirects unknown routes to Account.
const LETTERS_ROUTE = 'Letters' as AppRoute;

interface Props {
  navigation: any;
}

export default function EmployeeDashboardScreen({ navigation }: Props) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const language: 'en' | 'ar' = i18n.language?.startsWith('ar') ? 'ar' : 'en';
  const { user } = useAuthStore();
  const [dashboard, setDashboard] = useState<HomeDashboard | null>(null);
  const [signals, setSignals] = useState<EmployeeHomeSignals | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [punchFeedback, setPunchFeedback] = useState<PunchType | null>(null);
  const [selfieAttached, setSelfieAttached] = useState(false);
  const [pendingOpen, setPendingOpen] = useState(false);
  const [announcementSheet, setAnnouncementSheet] = useState<{ mode: 'one'; item: Announcement } | { mode: 'all' } | null>(null);
  const loadedOnce = useRef(false);

  const load = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    const [dash, sig] = await Promise.allSettled([
      dashboardApi.getEmployeeDashboard(),
      employeeHomeApi.getSignals(),
    ]);
    if (dash.status === 'fulfilled') {
      setDashboard(dash.value);
      setLoadFailed(false);
    } else {
      console.warn('[Home] Dashboard load failed:', dash.reason);
      // Keep what is on screen after a failed refresh; only an empty screen shows the error card.
      setLoadFailed(true);
    }
    if (sig.status === 'fulfilled') setSignals(sig.value);
    loadedOnce.current = true;
    setLoading(false);
    setRefreshing(false);
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  // Coming back from a correction, a payslip or a request: refresh quietly.
  useFocusEffect(useCallback(() => {
    if (loadedOnce.current) void load(true);
  }, [load]));

  const onRefresh = useCallback(() => {
    setRefreshing(true);
    void load(true);
  }, [load]);

  const punchFlow = usePunchFlow({
    onPunched: async ({ punchType, selfieAttached: attached }) => {
      haptics.success();
      setPunchFeedback(punchType);
      setSelfieAttached(attached);
      await load(true);
    },
    onOpenConsent: () => navigateTo(navigation, 'SelfieConsent', user),
  });

  const go = useCallback(
    (route: AppRoute, params?: Record<string, unknown>) => navigateTo(navigation, route, user, params),
    [navigation, user],
  );

  // ---- derived ----------------------------------------------------------------------------
  const today = f.businessDate() ?? signals?.today ?? '';
  const fullName = dashboard?.profile?.fullName ?? user?.fullName ?? '';
  const name = displayFirstName(fullName);
  const period = greetingPeriod(new Date(), f.timeZone);
  const greeting = name
    ? t(`home.greeting.${period}` as const, { name })
    : t(`home.greeting.${period}NoName` as const);
  const gregorian = today ? f.date(today, 'full') : '';
  const hijri = today ? f.hijri(today) : '';
  const heroSubtitle = [gregorian, hijri].filter(Boolean).join(' · ');

  const attendance = dashboard?.todayAttendance;
  const dayState = useMemo<DayState>(() => {
    const holiday = signals?.holidays ? holidayOn(signals.holidays, today) : null;
    return classifyDay({
      clockIn: attendance?.clockIn,
      clockOut: attendance?.clockOut,
      currentlyActive: Boolean(attendance?.currentlyActive),
      processedStatus: dashboard?.todayProcessedStatus ?? null,
      approvedLeaveToday: signals?.approvedLeaveToday ?? null,
      holidayToday: holiday,
      restDayByHistory: signals?.dayRecords ? restDayFromHistory(today, signals.dayRecords) : false,
      language,
    });
  }, [attendance, dashboard?.todayProcessedStatus, signals, today, language]);

  const annual = pickAnnualBalance(dashboard?.leaveBalances);
  const pending = useMemo(() => {
    if (!signals) return null;
    const parts: PendingPart[] = [
      { source: 'leave', count: signals.pending.leave },
      { source: 'overtime', count: signals.pending.overtime },
      { source: 'hr', count: signals.pending.hr },
    ];
    return summarizePending(parts);
  }, [signals]);

  const needs = useMemo<NeedsItem[]>(() => {
    if (!dashboard || !signals) return [];
    return buildNeedsYou({
      today,
      documents: dashboard.expiringDocuments.map((d) => ({ id: d.id, documentType: d.documentType, expiryDate: d.expiryDate })),
      dayRecords: signals.dayRecords ?? [],
      latestPayslip: signals.latestPayslip,
      seenPayslipId: signals.seenPayslipId,
    });
  }, [dashboard, signals, today]);

  const holiday = signals?.holidays ? nextHoliday(signals.holidays, today) : null;
  const announcements = dashboard?.announcements ?? [];
  const unread = signals?.unreadNotifications ?? dashboard?.unreadNotifications;
  const showContent = Boolean(dashboard);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        style={styles.scroll}
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={onRefresh}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          hideBack
          title={greeting}
          subtitle={heroSubtitle || undefined}
          actions={
            <View style={styles.heroActions}>
              <GlassIconButton
                icon="notifications-outline"
                label={t('home.notificationsA11y')}
                badge={unread ?? undefined}
                onPress={() => go('Notifications')}
              />
              <MotionPressable
                onPress={() => go('Profile')}
                accessibilityRole="button"
                accessibilityLabel={t('home.profileA11y')}
                contentStyle={styles.avatarButton}
              >
                <EmployeeAvatar
                  name={fullName}
                  photoUrl={dashboard?.profile?.profilePhotoUrl ?? user?.profilePhotoUrl}
                  size={44}
                  ring
                />
              </MotionPressable>
            </View>
          }
        />

        {loading && !showContent ? (
          <HomeSkeleton />
        ) : !showContent ? (
          <View style={styles.section}>
            <StateCard
              variant={loadFailed ? 'error' : 'empty'}
              title={t('home.loadErrorTitle')}
              body={t('home.loadErrorBody')}
              onRetry={() => void load()}
            />
          </View>
        ) : (
          <Animated.View entering={FadeIn.duration(theme.motion.durations.base)} style={styles.stack}>
            {/* 1. Today */}
            <View style={styles.section}>
              <AttendanceCard
                attendance={attendance}
                canClockIn={!attendance?.currentlyActive}
                canClockOut={Boolean(attendance?.currentlyActive)}
                punchLoading={punchFlow.punching}
                onClockIn={() => void punchFlow.punch('CLOCK_IN')}
                onClockOut={() => void punchFlow.punch('CLOCK_OUT')}
                onViewHistory={() => go('AttendanceHistory')}
                feedback={punchFeedback}
                selfieAttached={selfieAttached}
                notes={punchFlow.notes}
                day={dayState}
                workedMinutes={dashboard?.todayWorkedMinutes ?? null}
              />
            </View>

            {/* 2. KPIs */}
            <View style={[styles.section, styles.kpiRow]}>
              {annual ? (
                <KpiTile
                  style={styles.kpi}
                  label={t('home.kpi.leaveLabel')}
                  value={f.number(annual.available)}
                  period={t('home.kpi.leavePeriod', { year: today.slice(0, 4) })}
                  caption={t('home.kpi.leaveCaption', { used: f.number(annual.used), pending: f.number(annual.pending) })}
                  accessibilityLabel={t('home.kpi.leaveA11y', {
                    available: f.number(annual.available),
                    entitled: f.number(annual.allocated),
                    year: today.slice(0, 4),
                  })}
                  onPress={() => go('ApplyLeave', { segment: 'history' })}
                />
              ) : null}
              {pending ? (
                <KpiTile
                  style={styles.kpi}
                  label={t('home.kpi.pendingLabel')}
                  value={pending.total === null ? null : f.number(pending.total)}
                  period={t('home.kpi.pendingPeriod')}
                  caption={pending.partial ? t('home.kpi.pendingPartial') : t('home.kpi.pendingCaption')}
                  accessibilityHint={t('home.kpi.pendingHint')}
                  onPress={() => setPendingOpen(true)}
                />
              ) : (
                <Surface style={styles.kpi} contentStyle={styles.kpiSkeleton}>
                  <Skeleton width="70%" height={12} />
                  <Skeleton width="35%" height={24} />
                  <Skeleton width="55%" height={10} />
                </Surface>
              )}
            </View>

            {/* 3. Needs you */}
            <NeedsYou
              loading={!signals}
              items={needs}
              f={f}
              today={today}
              onAction={(item) => {
                if (item.kind === 'missingPunch') go('AttendanceCorrection', { date: item.date });
                else if (item.kind === 'payslip') {
                  void employeeHomeApi.markPayslipSeen(item.payslipId);
                  go('PayslipDetail', { id: item.payslipId });
                } else go('Documents');
              }}
            />

            {/* 4. Quick actions */}
            <View style={styles.section}>
              <SectionHeader title={t('home.quick.title')} />
              <View style={styles.quickGrid}>
                <QuickAction icon="calendar-outline" label={t('home.quick.leave')} onPress={() => go('ApplyLeave', { segment: 'request' })} />
                <QuickAction icon="time-outline" label={t('home.quick.overtime')} onPress={() => go('Overtime')} />
                <QuickAction icon="wallet-outline" label={t('home.quick.payslips')} onPress={() => go('Payslips')} />
                <QuickAction icon="document-text-outline" label={t('home.quick.salaryCertificate')} onPress={() => go(LETTERS_ROUTE)} />
              </View>
            </View>

            {/* 5. Next public holiday */}
            {holiday ? <HolidayCard holiday={holiday} today={today} f={f} language={language} /> : null}

            {/* 6. Announcements */}
            {announcements.length > 0 ? (
              <View style={styles.section}>
                <SectionHeader
                  title={t('home.announcements.title')}
                  actionLabel={announcements.length > 2 ? t('home.announcements.seeAll') : undefined}
                  onAction={() => setAnnouncementSheet({ mode: 'all' })}
                />
                <Surface contentStyle={styles.listCard}>
                  {announcements.slice(0, 2).map((item, index) => (
                    <AnnouncementRow
                      key={item.id}
                      item={item}
                      f={f}
                      first={index === 0}
                      onPress={() => setAnnouncementSheet({ mode: 'one', item })}
                    />
                  ))}
                </Surface>
              </View>
            ) : null}
          </Animated.View>
        )}
      </ScrollView>

      {punchFlow.modal}

      <PendingSheet
        visible={pendingOpen}
        parts={pending?.parts ?? []}
        f={f}
        onClose={() => setPendingOpen(false)}
        onOpen={(source) => {
          setPendingOpen(false);
          if (source === 'leave') go('ApplyLeave', { segment: 'history' });
          else if (source === 'overtime') go('Overtime');
          else go('HRRequests');
        }}
      />

      <AnnouncementsSheet
        state={announcementSheet}
        initial={announcements}
        f={f}
        onClose={() => setAnnouncementSheet(null)}
      />
    </View>
  );
}

// ==========================================================================================
// Loading
// ==========================================================================================

function HomeSkeleton() {
  return (
    <View style={styles.stack}>
      <View style={styles.section}>
        <SkeletonCard lines={4} />
      </View>
      <View style={[styles.section, styles.kpiRow]}>
        <SkeletonCard lines={2} style={styles.kpi} />
        <SkeletonCard lines={2} style={styles.kpi} />
      </View>
      <View style={styles.section}>
        <SkeletonCard lines={2} />
      </View>
    </View>
  );
}

// ==========================================================================================
// 1. Time card (also used by the manager home; extra props are optional)
// ==========================================================================================

export interface AttendanceCardProps {
  attendance?: TodayAttendance;
  canClockIn: boolean;
  canClockOut: boolean;
  punchLoading: boolean;
  onClockIn: () => void;
  onClockOut: () => void;
  onViewHistory: () => void;
  feedback: PunchType | null;
  /** The server stored this punch with a selfie ("selfie attached"; no face matching is claimed). */
  selfieAttached?: boolean;
  /** What applies to this employee's punch (selfie consent, site location check). */
  notes?: AttendanceVerificationNotes;
  /**
   * Today's classification from evidence (employeeHomeModel.classifyDay). Without it the card reads
   * the punches alone and never shows "Absent" — no punch is "not recorded yet".
   */
  day?: DayState;
  /** Minutes worked today as the server recorded them (breaks deducted when processed). */
  workedMinutes?: number | null;
}

const NON_WORKING: readonly DayState['kind'][] = ['restDay', 'holiday', 'onLeave'];

export function AttendanceCard({
  attendance,
  canClockIn,
  canClockOut,
  punchLoading,
  onClockIn,
  onClockOut,
  onViewHistory,
  feedback,
  selfieAttached,
  notes,
  day,
  workedMinutes,
}: AttendanceCardProps) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const state = day ?? classifyDay({
    clockIn: attendance?.clockIn,
    clockOut: attendance?.clockOut,
    currentlyActive: Boolean(attendance?.currentlyActive),
  });

  // The "on the clock for" line moves with the clock, once a minute.
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    if (state.kind !== 'onClock') return;
    setNow(new Date());
    const timer = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(timer);
  }, [state.kind]);

  const duration = (minutes: number | null) => {
    if (minutes === null) return null;
    const { h, m } = splitMinutes(minutes);
    if (h === 0) return t('home.duration.m', { m: f.number(m) });
    if (m === 0) return t('home.duration.h', { h: f.number(h) });
    return t('home.duration.hm', { h: f.number(h), m: f.number(m) });
  };

  const clockIn = attendance?.clockIn ? f.time(attendance.clockIn) : null;
  const clockOut = attendance?.clockOut ? f.time(attendance.clockOut) : null;
  const shift = attendance?.shiftStart && attendance?.shiftEnd
    ? t('home.time.shift', { start: f.time(attendance.shiftStart), end: f.time(attendance.shiftEnd) })
    : null;

  const statusLabel = (() => {
    switch (state.kind) {
      case 'onClock': return t('home.status.ON_CLOCK');
      case 'done': return state.recordStatus === 'LATE' ? t('home.status.LATE') : t('home.status.DONE');
      case 'restDay': return t('home.status.WEEKEND');
      case 'holiday': return t('home.status.HOLIDAY');
      case 'onLeave': return t('home.status.ON_LEAVE');
      case 'absent': return t('home.status.ABSENT');
      case 'missingPunch': return t('home.status.MISSING_PUNCH');
      default: return t('home.status.NOT_RECORDED');
    }
  })();

  let headline: string;
  let detail: string | null;
  switch (state.kind) {
    case 'onClock':
      headline = clockIn ? t('home.time.clockedInAt', { time: clockIn }) : t('home.status.ON_CLOCK');
      detail = (() => {
        const d = duration(minutesBetween(attendance?.clockIn, now));
        return d ? t('home.time.onClockFor', { duration: d }) : shift;
      })();
      break;
    case 'done': {
      const d = duration(workedMinutes ?? minutesBetween(attendance?.clockIn, attendance?.clockOut));
      headline = d ? t('home.time.dayComplete', { duration: d }) : t('home.time.dayCompleteNoDuration');
      detail = clockIn && clockOut ? t('home.time.range', { in: clockIn, out: clockOut }) : null;
      break;
    }
    case 'restDay':
      headline = t('home.status.WEEKEND');
      detail = t('home.time.nonWorking');
      break;
    case 'holiday':
      headline = state.holidayName ?? t('home.status.HOLIDAY');
      detail = t('home.time.nonWorking');
      break;
    case 'onLeave':
      headline = t('home.status.ON_LEAVE');
      detail = state.leaveTypeName ?? t('home.time.nonWorking');
      break;
    case 'absent':
    case 'missingPunch':
      headline = statusLabel;
      detail = clockIn ? t('home.time.clockedInAt', { time: clockIn }) : shift;
      break;
    default:
      headline = t('home.time.notStarted');
      detail = shift;
  }

  const offDay = NON_WORKING.includes(state.kind);
  const done = state.kind === 'done';

  return (
    <Surface level={2} radius={theme.radius.xl} contentStyle={styles.timeCard}>
      <View style={styles.timeTop}>
        <Text accessibilityRole="header" style={[theme.typography.subheadline, styles.flex, { color: theme.colors.textSecondary }]}>
          {t('home.time.title')}
        </Text>
        <StatusPill tone={state.tone} label={statusLabel} />
      </View>

      <View style={styles.timeCopy}>
        <Text style={[theme.typography.title2, { color: theme.colors.text }]}>{headline}</Text>
        {detail ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary, fontVariant: ['tabular-nums'] }]}>
            {detail}
          </Text>
        ) : null}
        {attendance?.workLocation ? (
          <View style={styles.inlineRow}>
            <Ionicons accessible={false} name="location-outline" size={15} color={theme.colors.textMuted} />
            <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}>
              {attendance.workLocation}
            </Text>
          </View>
        ) : null}
      </View>

      {feedback ? (
        <Animated.View
          entering={FadeInDown.duration(theme.motion.durations.base)}
          accessible
          accessibilityLiveRegion="polite"
          style={[styles.feedback, { backgroundColor: theme.colors.status.success.tint }]}
        >
          <Ionicons accessible={false} name="checkmark-circle" size={20} color={theme.colors.status.success.fg} />
          <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.text }]}>
            {feedback === 'CLOCK_IN' ? t('home.time.clockedInFeedback') : t('home.time.clockedOutFeedback')}
            {selfieAttached ? ` ${t('selfie.punch.selfieAttached')}` : ''}
          </Text>
        </Animated.View>
      ) : null}

      {notes?.geofence ? (
        <View style={styles.inlineRow}>
          <Ionicons accessible={false} name="navigate-circle-outline" size={16} color={theme.colors.textMuted} />
          <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}>{notes.geofence}</Text>
        </View>
      ) : null}
      {notes?.selfie ? (
        <View style={styles.inlineRow}>
          <Ionicons accessible={false} name="camera-outline" size={16} color={theme.colors.textMuted} />
          <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}>{notes.selfie.text}</Text>
          <MotionPressable
            onPress={notes.selfie.onPress}
            accessibilityRole="button"
            accessibilityLabel={`${notes.selfie.actionLabel}. ${notes.selfie.text}`}
            contentStyle={styles.textButton}
          >
            <Text style={[theme.typography.footnote, styles.strong, { color: theme.colors.accent }]}>{notes.selfie.actionLabel}</Text>
          </MotionPressable>
        </View>
      ) : null}

      {/* One action. Off days and a completed day keep a quiet way to punch anyway. */}
      {done || offDay ? (
        canClockIn ? (
          <LiquidButton
            label={done ? t('home.time.clockInAgain') : t('home.time.clockIn')}
            accessibilityHint={done ? t('home.time.clockInAgainHint') : undefined}
            icon="log-in-outline"
            variant="secondary"
            size="compact"
            onPress={onClockIn}
            loading={punchLoading}
            disabled={punchLoading}
          />
        ) : null
      ) : canClockOut ? (
        <LiquidButton
          label={t('home.time.clockOut')}
          icon="log-out-outline"
          onPress={onClockOut}
          loading={punchLoading}
          disabled={punchLoading}
        />
      ) : canClockIn ? (
        <LiquidButton
          label={t('home.time.clockIn')}
          icon="log-in-outline"
          onPress={onClockIn}
          loading={punchLoading}
          disabled={punchLoading}
        />
      ) : null}

      <MotionPressable
        onPress={onViewHistory}
        accessibilityRole="button"
        accessibilityLabel={t('home.time.history')}
        contentStyle={styles.historyLink}
      >
        <Text style={[theme.typography.subheadline, styles.strong, { color: theme.colors.accent }]}>{t('home.time.history')}</Text>
        <DirectionalIcon name="chevron" size={16} color={theme.colors.accent} />
      </MotionPressable>
    </Surface>
  );
}

// ==========================================================================================
// 3. Needs you
// ==========================================================================================

const DOC_KEYS: Record<DocumentKind, 'home.docTypes.iqama' | 'home.docTypes.passport' | 'home.docTypes.nationalId' | 'home.docTypes.visa' | 'home.docTypes.workPermit' | 'home.docTypes.contract' | 'home.docTypes.drivingLicense' | 'home.docTypes.medical' | 'home.docTypes.document'> = {
  iqama: 'home.docTypes.iqama',
  passport: 'home.docTypes.passport',
  nationalId: 'home.docTypes.nationalId',
  visa: 'home.docTypes.visa',
  workPermit: 'home.docTypes.workPermit',
  contract: 'home.docTypes.contract',
  drivingLicense: 'home.docTypes.drivingLicense',
  medical: 'home.docTypes.medical',
  document: 'home.docTypes.document',
};

function NeedsYou({
  loading,
  items,
  f,
  today,
  onAction,
}: {
  loading: boolean;
  items: NeedsItem[];
  f: UseFormat;
  today: string;
  onAction: (item: NeedsItem) => void;
}) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();

  if (loading) {
    return (
      <View style={styles.section}>
        <SectionHeader title={t('home.needs.title')} />
        <SkeletonCard lines={2} />
      </View>
    );
  }

  if (items.length === 0) {
    return (
      <View style={styles.section}>
        <Surface level={0} contentStyle={styles.allClear}>
          <Ionicons accessible={false} name="checkmark-circle-outline" size={20} color={theme.colors.status.success.fg} />
          <Text style={[theme.typography.subheadline, styles.flex, { color: theme.colors.textSecondary }]}>
            {t('home.needs.allClear')}
          </Text>
        </Surface>
      </View>
    );
  }

  const describe = (item: NeedsItem): { icon: IconName; tone: 'danger' | 'warning' | 'info'; title: string; body: string; verb: string } => {
    if (item.kind === 'missingPunch') {
      return {
        icon: 'finger-print-outline',
        tone: 'danger',
        title: t('home.needs.missingPunchTitle', { date: f.date(item.date, 'dayMonth') }),
        body: t('home.needs.missingPunchBody'),
        verb: t('home.needs.fixPunch'),
      };
    }
    if (item.kind === 'payslip') {
      return {
        icon: 'wallet-outline',
        tone: 'info',
        title: t('home.needs.payslipTitle', { period: f.date(`${item.year}-${String(item.month).padStart(2, '0')}-01`, 'monthYear') }),
        body: t('home.needs.payslipBody'),
        verb: t('home.needs.view'),
      };
    }
    const doc = item.docKind === 'document' ? item.documentType : t(DOC_KEYS[item.docKind]);
    const date = f.date(item.expiryDate, 'medium');
    if (item.kind === 'documentExpired') {
      return { icon: 'alert-circle-outline', tone: 'danger', title: t('home.needs.docExpiredTitle', { doc }), body: t('home.needs.docExpiredBody', { date }), verb: t('home.needs.update') };
    }
    const title = item.daysLeft === 0
      ? t('home.needs.docExpiringToday', { doc })
      : t('home.needs.docExpiringTitle', { doc, duration: dayDistance(today, item.expiryDate, i18n.language) });
    return { icon: 'id-card-outline', tone: 'warning', title, body: t('home.needs.docExpiringBody', { date }), verb: t('home.needs.update') };
  };

  return (
    <View style={styles.section}>
      <SectionHeader title={t('home.needs.title')} />
      <Surface contentStyle={styles.listCard}>
        {items.map((item, index) => {
          const d = describe(item);
          const tone = theme.colors.status[d.tone];
          return (
            <View
              key={item.key}
              style={[styles.needsRow, index > 0 && { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: theme.colors.hairline }]}
            >
              <View style={[styles.rowIcon, { backgroundColor: tone.tint }]}>
                <Ionicons accessible={false} name={d.icon} size={20} color={tone.fg} />
              </View>
              <View style={styles.flex} accessible accessibilityLabel={`${d.title}. ${d.body}`}>
                <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{d.title}</Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{d.body}</Text>
              </View>
              <LiquidButton
                label={d.verb}
                variant="secondary"
                size="compact"
                accessibilityHint={d.title}
                onPress={() => onAction(item)}
              />
            </View>
          );
        })}
      </Surface>
    </View>
  );
}

/** "12 days" / "١٢ يومًا" — date-fns words the plural correctly in Arabic. */
function dayDistance(fromDay: string, toDay: string, language?: string): string {
  const from = new Date(`${fromDay}T00:00:00Z`);
  const to = new Date(`${toDay}T00:00:00Z`);
  return formatDistanceStrict(to, from, { unit: 'day', locale: language?.startsWith('ar') ? arLocale : enUS });
}

// ==========================================================================================
// 4. Quick actions
// ==========================================================================================

function QuickAction({ icon, label, onPress }: { icon: IconName; label: string; onPress: () => void }) {
  const { theme } = useTheme();
  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      style={styles.quickCell}
      contentStyle={styles.fill}
    >
      <Surface style={styles.fill} contentStyle={styles.quickTile}>
        <View style={[styles.rowIcon, { backgroundColor: theme.colors.status.info.tint }]}>
          <Ionicons accessible={false} name={icon} size={20} color={theme.colors.accent} />
        </View>
        <Text numberOfLines={2} style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>
          {label}
        </Text>
      </Surface>
    </MotionPressable>
  );
}

// ==========================================================================================
// 5. Holiday
// ==========================================================================================

function HolidayCard({
  holiday,
  today,
  f,
  language,
}: {
  holiday: PublicHolidayItem;
  today: string;
  f: UseFormat;
  language: 'en' | 'ar';
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const days = daysBetween(today, holiday.date);
  const when = days === 0
    ? t('home.holiday.today')
    : days === 1
      ? t('home.holiday.tomorrow')
      : t('home.holiday.inDuration', { duration: dayDistance(today, holiday.date, language) });
  const name = language === 'ar' ? holiday.nameAr || holiday.nameEn : holiday.nameEn;
  const hijri = f.hijri(holiday.date) || holiday.hijriDate || '';
  const tone = theme.colors.status.info;

  return (
    <View style={styles.section}>
      <SectionHeader title={t('home.holiday.title')} />
      <Surface
        contentStyle={styles.holidayCard}
        accessible
        accessibilityLabel={[name, f.date(holiday.date, 'full'), hijri, when].filter(Boolean).join(', ')}
      >
        <View style={[styles.dateBlock, { backgroundColor: tone.tint }]}>
          <Text style={[theme.typography.title2, { color: tone.fg }]}>{f.number(Number(holiday.date.slice(8, 10)))}</Text>
          <Text style={[theme.typography.footnote, { color: tone.fg }]}>{f.date(holiday.date, 'weekday')}</Text>
        </View>
        <View style={styles.flex}>
          <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{name}</Text>
          <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{f.date(holiday.date, 'full')}</Text>
          {hijri ? (
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{hijri}</Text>
          ) : null}
        </View>
        <StatusPill tone="info" label={when} />
      </Surface>
    </View>
  );
}

// ==========================================================================================
// 6. Announcements
// ==========================================================================================

function AnnouncementRow({
  item,
  f,
  first,
  onPress,
}: {
  item: Announcement;
  f: UseFormat;
  first: boolean;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const published = item.publishedAt ? t('home.announcements.published', { date: f.date(item.publishedAt, 'medium') }) : null;
  return (
    <MotionPressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityLabel={[item.title, published].filter(Boolean).join('. ')}
      contentStyle={[styles.announcementRow, !first && { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: theme.colors.hairline }]}
    >
      <View style={styles.flex}>
        <Text numberOfLines={2} style={[theme.typography.headline, { color: theme.colors.text }]}>{item.title}</Text>
        {item.body ? (
          <Text numberOfLines={2} style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{item.body}</Text>
        ) : null}
        {published ? <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{published}</Text> : null}
      </View>
      <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
    </MotionPressable>
  );
}

function AnnouncementsSheet({
  state,
  initial,
  f,
  onClose,
}: {
  state: { mode: 'one'; item: Announcement } | { mode: 'all' } | null;
  initial: Announcement[];
  f: UseFormat;
  onClose: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const [all, setAll] = useState<Announcement[] | null>(null);
  const [failed, setFailed] = useState(false);
  const mode = state?.mode;

  const loadAll = useCallback(async () => {
    setFailed(false);
    setAll(null);
    try {
      setAll(await announcementsApi.getAll());
    } catch {
      setFailed(true);
    }
  }, []);

  useEffect(() => {
    if (mode === 'all') void loadAll();
  }, [mode, loadAll]);

  const list = state?.mode === 'one' ? [state.item] : all ?? [];

  return (
    <BottomSheet
      visible={state !== null}
      onClose={onClose}
      title={state?.mode === 'one' ? state.item.title : t('home.announcements.title')}
    >
      {state?.mode === 'all' && failed ? (
        <StateCard variant="error" onRetry={() => void loadAll()} />
      ) : state?.mode === 'all' && !all ? (
        <View style={styles.sheetList}>
          {initial.slice(0, 3).map((a) => <SkeletonCard key={a.id} lines={2} />)}
        </View>
      ) : (
        <View style={styles.sheetList}>
          {list.map((a) => (
            <View key={a.id} style={styles.sheetItem}>
              {state?.mode === 'all' ? (
                <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{a.title}</Text>
              ) : null}
              {a.publishedAt ? (
                <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                  {t('home.announcements.published', { date: f.date(a.publishedAt, 'long') })}
                </Text>
              ) : null}
              {a.body ? <Text style={[theme.typography.body, { color: theme.colors.text }]}>{a.body}</Text> : null}
            </View>
          ))}
        </View>
      )}
    </BottomSheet>
  );
}

// ==========================================================================================
// Pending breakdown
// ==========================================================================================

function PendingSheet({
  visible,
  parts,
  f,
  onClose,
  onOpen,
}: {
  visible: boolean;
  parts: PendingPart[];
  f: UseFormat;
  onClose: () => void;
  onOpen: (source: PendingPart['source']) => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const labels: Record<PendingPart['source'], string> = {
    leave: t('home.pendingSheet.leave'),
    overtime: t('home.pendingSheet.overtime'),
    hr: t('home.pendingSheet.hr'),
  };
  const icons: Record<PendingPart['source'], IconName> = {
    leave: 'calendar-outline',
    overtime: 'time-outline',
    hr: 'chatbubble-ellipses-outline',
  };

  return (
    <BottomSheet visible={visible} onClose={onClose} title={t('home.pendingSheet.title')} subtitle={t('home.pendingSheet.subtitle')}>
      <Surface contentStyle={styles.listCard}>
        {parts.map((part, index) => {
          const count = part.count === null ? t('home.pendingSheet.unavailable') : f.number(part.count);
          return (
            <MotionPressable
              key={part.source}
              onPress={() => onOpen(part.source)}
              accessibilityRole="button"
              accessibilityLabel={t('home.pendingSheet.openA11y', { label: labels[part.source], value: count })}
              contentStyle={[styles.sheetRow, index > 0 && { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: theme.colors.hairline }]}
            >
              <View style={[styles.rowIcon, { backgroundColor: theme.colors.status.info.tint }]}>
                <Ionicons accessible={false} name={icons[part.source]} size={20} color={theme.colors.accent} />
              </View>
              <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{labels[part.source]}</Text>
              <Text
                style={[
                  part.count === null ? theme.typography.footnote : theme.typography.title3,
                  { color: part.count === null ? theme.colors.textMuted : theme.colors.text, fontVariant: ['tabular-nums'] },
                ]}
              >
                {count}
              </Text>
              <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
            </MotionPressable>
          );
        })}
      </Surface>
    </BottomSheet>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flex: 1, backgroundColor: 'transparent' },
  content: { paddingBottom: 24 },
  stack: { gap: 24 },
  section: { paddingHorizontal: 16 },
  flex: { flex: 1, minWidth: 0 },
  fill: { flex: 1 },
  strong: { fontWeight: '600' },
  heroActions: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  avatarButton: { width: 44, height: 44, borderRadius: 22 },

  // Time card
  timeCard: { padding: 18, gap: 14 },
  timeTop: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  timeCopy: { gap: 4 },
  inlineRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  feedback: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 12, borderRadius: 12 },
  textButton: { minHeight: 44, paddingHorizontal: 8, justifyContent: 'center' },
  historyLink: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 4,
    marginTop: -6,
    marginBottom: -8,
  },

  // KPIs
  kpiRow: { flexDirection: 'row', gap: 12, alignItems: 'stretch' },
  kpi: { flex: 1 },
  kpiSkeleton: { padding: 14, gap: 10, minHeight: 104 },

  // Lists
  listCard: { paddingHorizontal: 14 },
  needsRow: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 14 },
  rowIcon: { width: 40, height: 40, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  allClear: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 14, paddingVertical: 12 },

  // Quick actions
  quickGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  quickCell: { flexBasis: '47%', flexGrow: 1, minHeight: 72 },
  quickTile: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 14 },

  // Holiday
  holidayCard: { flexDirection: 'row', alignItems: 'center', gap: 14, padding: 14 },
  dateBlock: { width: 56, minHeight: 60, borderRadius: 14, alignItems: 'center', justifyContent: 'center', paddingVertical: 6 },

  // Announcements
  announcementRow: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 14, minHeight: 44 },

  // Sheets
  sheetList: { gap: 16 },
  sheetItem: { gap: 6 },
  sheetRow: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 12, minHeight: 56 },
});
