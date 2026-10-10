// Manager Home — exception-first: the decisions waiting for you lead, then your team today, who's
// out next, and your own attendance last. Every number names its period, carries its definition,
// and opens the records behind it. The counts come from the same model as the Approvals and Team
// screens (features/approvals/approvalModel + teamModel), so Home and the lists always agree.

import React, { useCallback, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { approvalsApi, attendanceApi, teamApi } from '@/api/services';
import { teamCalendarApi } from '@/api/teamCalendar';
import { usePunchFlow } from '@/features/attendance/usePunchFlow';
import { navigateTo, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  DirectionalIcon,
  EmployeeAvatar,
  GlassIconButton,
  KpiTile,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import { givenName, summarizeInbox, type ApprovalKind, type InboxItem } from '@/features/approvals/approvalModel';
import {
  addDays,
  BUCKET_TONE,
  peopleOut,
  teamTodayCounts,
  type OutEntry,
  type TeamBucket,
  type TeamDay,
  type TeamMemberToday,
} from '@/features/approvals/teamModel';
import { useManagerText } from '@/features/approvals/useManagerText';
import type { PunchType, TodayAttendance } from '@/types';

interface Props {
  navigation: any;
}

const WHOS_OUT_DAYS = 14;

type Load<T> = { state: 'loading' | 'ready' | 'failed'; data: T | null };
const loading = <T,>(): Load<T> => ({ state: 'loading', data: null });

type TeamTodayData = {
  members: TeamMemberToday[];
  day: TeamDay;
  holiday: { nameEn: string | null; nameAr: string | null } | null;
  businessDate: string;
  loadedAt: Date;
};

export default function ManagerDashboardScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const [refreshing, setRefreshing] = useState(false);
  const [inbox, setInbox] = useState<Load<{ items: InboxItem[]; unavailable: string[] }>>(loading);
  const [team, setTeam] = useState<Load<TeamTodayData>>(loading);
  const [out, setOut] = useState<Load<OutEntry[]>>(loading);
  const [attendance, setAttendance] = useState<Load<TodayAttendance>>(loading);
  const [punchFeedback, setPunchFeedback] = useState<{ type: PunchType; selfie: boolean } | null>(null);

  const employeeId = Number(user?.employeeId);
  const hasEmployeeProfile = Number.isFinite(employeeId) && employeeId > 0;
  const go = useCallback(
    (route: AppRoute, params?: Record<string, unknown>) => navigateTo(navigation, route, user, params),
    [navigation, user],
  );

  const loadAttendance = useCallback(async () => {
    if (!hasEmployeeProfile) return;
    try {
      setAttendance({ state: 'ready', data: await attendanceApi.getTodayAttendance() });
    } catch {
      setAttendance((prev) => ({ state: 'failed', data: prev.data }));
    }
  }, [hasEmployeeProfile]);

  const load = useCallback(async () => {
    const today = f.businessDate() ?? new Date().toISOString().slice(0, 10);
    await Promise.all([
      approvalsApi.getInbox().then(
        (data) => setInbox({ state: 'ready', data }),
        () => setInbox((prev) => ({ state: 'failed', data: prev.data })),
      ),
      teamApi.getTeamToday().then(
        (data) => setTeam({ state: 'ready', data: { ...data, loadedAt: new Date() } }),
        () => setTeam((prev) => ({ state: 'failed', data: prev.data })),
      ),
      teamCalendarApi.getTeamLeave(today, addDays(today, WHOS_OUT_DAYS - 1)).then(
        (data) => setOut({ state: 'ready', data }),
        () => setOut((prev) => ({ state: 'failed', data: prev.data })),
      ),
      loadAttendance(),
    ]);
    setRefreshing(false);
  }, [f, loadAttendance]);

  // Fresh on every visit, so a decision made in Approvals is reflected the moment you come back.
  useFocusEffect(
    useCallback(() => {
      void load();
    }, [load]),
  );

  // Punches here are the signed-in manager's own (self-only since #209); selfie, consent and
  // location rules are the same as on the employee home.
  const punchFlow = usePunchFlow({
    onPunched: async ({ punchType, selfieAttached }) => {
      haptics.success();
      setPunchFeedback({ type: punchType, selfie: selfieAttached });
      await loadAttendance();
    },
    onOpenConsent: () => navigateTo(navigation, 'SelfieConsent', user),
  });

  const name = givenName(user?.fullName);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void load();
            }}
            tintColor={theme.colors.accent}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={f.date(new Date(), 'full')}
          title={name ? t('manager.home.title', { name }) : t('manager.home.titleNoName')}
          actions={
            <View style={styles.heroActions}>
              <MotionPressable
                onPress={() => go('Profile')}
                accessibilityRole="button"
                accessibilityLabel={t('manager.home.openProfile')}
                contentStyle={styles.avatarButton}
              >
                <EmployeeAvatar name={user?.fullName ?? ''} photoUrl={user?.profilePhotoUrl} size={44} ring />
              </MotionPressable>
              <GlassIconButton icon="notifications-outline" label={t('manager.home.notifications')} onPress={() => go('Notifications')} />
            </View>
          }
        />

        <View style={styles.section}>
          <DecisionsHero inbox={inbox} onRetry={() => void load()} go={go} />
        </View>

        <View style={styles.section}>
          <TeamTodaySection team={team} onRetry={() => void load()} go={go} />
        </View>

        <View style={styles.section}>
          <WhosOutCard out={out} go={go} />
        </View>

        <View style={styles.section}>
          {hasEmployeeProfile ? (
            <MyDayCard
              attendance={attendance}
              punching={punchFlow.punching}
              feedback={punchFeedback}
              notes={punchFlow.notes}
              onPunch={(type) => {
                setPunchFeedback(null);
                void punchFlow.punch(type);
              }}
              onHistory={() => go('AttendanceHistory')}
              onRetry={() => void loadAttendance()}
            />
          ) : (
            <Surface level={0} contentStyle={styles.notice}>
              <Ionicons accessible={false} name="person-add-outline" size={20} color={theme.colors.textSecondary} />
              <View style={styles.flex}>
                <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.home.myDay.noProfileTitle')}</Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('manager.home.myDay.noProfileBody')}</Text>
              </View>
            </Surface>
          )}
        </View>

        <View style={styles.section}>
          <Text accessibilityRole="header" style={[theme.typography.title3, styles.sectionTitle, { color: theme.colors.text }]}>
            {t('manager.home.forYou.title')}
          </Text>
          <Surface contentStyle={styles.linkList}>
            <LinkRow icon="calendar-outline" label={t('manager.home.forYou.myLeave')} onPress={() => go('ApplyLeave')} />
            <LinkRow icon="document-text-outline" label={t('manager.home.forYou.payslips')} onPress={() => go('Payslips')} />
            <LinkRow icon="chatbubble-ellipses-outline" label={t('manager.home.forYou.hrRequests')} onPress={() => go('HRRequests')} last />
          </Surface>
        </View>
      </ScrollView>
      {hasEmployeeProfile ? punchFlow.modal : null}
    </View>
  );
}

function DecisionsHero({
  inbox,
  onRetry,
  go,
}: {
  inbox: Load<{ items: InboxItem[]; unavailable: string[] }>;
  onRetry: () => void;
  go: (route: AppRoute, params?: Record<string, unknown>) => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const summary = useMemo(() => summarizeInbox(inbox.data?.items ?? []), [inbox.data]);

  if (inbox.state === 'loading' && !inbox.data) return <SkeletonCard lines={3} />;
  if (inbox.state === 'failed' && !inbox.data) {
    return <StateCard variant="error" title={t('manager.home.decisions.errorTitle')} onRetry={onRetry} />;
  }

  if (summary.total === 0) {
    return (
      <Surface level={2} contentStyle={styles.hero}>
        <View style={styles.row}>
          <Ionicons accessible={false} name="checkmark-circle" size={26} color={theme.colors.status.success.fg} />
          <Text style={[theme.typography.title3, styles.flex, { color: theme.colors.text }]}>{t('manager.home.decisions.clearTitle')}</Text>
        </View>
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('manager.home.decisions.clearBody')}</Text>
        {inbox.data?.unavailable.length ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.status.warning.fg }]}>{t('manager.approvals.partialOvertime')}</Text>
        ) : null}
        <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>{t('manager.home.decisions.definition')}</Text>
      </Surface>
    );
  }

  const lead = summary.lead;
  const leadLine = lead
    ? lead.isOverdue && lead.dueAt
      ? t('manager.home.decisions.leadOverdue', { kind: text.subject(lead), name: text.requester(lead), date: f.date(lead.dueAt, 'dayMonth') })
      : lead.submittedAt
        ? t('manager.home.decisions.lead', { kind: text.subject(lead), name: text.requester(lead), when: f.relative(lead.submittedAt) })
        : t('manager.home.decisions.leadNoDate', { kind: text.subject(lead), name: text.requester(lead) })
    : null;
  const kinds = (Object.keys(summary.byKind) as ApprovalKind[]).sort((a, b) => (summary.byKind[b] ?? 0) - (summary.byKind[a] ?? 0));

  return (
    <Surface level={2} contentStyle={styles.hero}>
      <View
        accessible
        accessibilityLabel={`${f.number(summary.total)} ${t('manager.home.decisions.waitingLabel')}`}
        style={styles.heroCount}
      >
        <Text style={[theme.typography.largeTitle, styles.tabular, { color: theme.colors.text }]}>{f.number(summary.total)}</Text>
        <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{t('manager.home.decisions.waitingLabel')}</Text>
      </View>
      {summary.overdue ? (
        <StatusPill tone="danger" icon="alert-circle" label={t('manager.home.decisions.overdue', { n: f.number(summary.overdue) })} />
      ) : null}
      {leadLine ? (
        <Text style={[theme.typography.subheadline, { color: lead?.isOverdue ? theme.colors.status.danger.fg : theme.colors.textSecondary }]}>
          {leadLine}
        </Text>
      ) : null}
      <View style={styles.kindRow}>
        {kinds.map((k) => (
          <MotionPressable
            key={k}
            haptic="selection"
            accessibilityRole="button"
            onPress={() => go('Approvals', { filterType: k })}
            contentStyle={[styles.kindChip, { backgroundColor: theme.colors.surfaceInset }]}
          >
            <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.text }]}>
              {t('manager.home.decisions.byKind', { kind: text.kind(k), n: f.number(summary.byKind[k] ?? 0) })}
            </Text>
          </MotionPressable>
        ))}
      </View>
      {inbox.data?.unavailable.length ? (
        <Text style={[theme.typography.footnote, { color: theme.colors.status.warning.fg }]}>{t('manager.approvals.partialOvertime')}</Text>
      ) : null}
      <LiquidButton label={t('manager.home.decisions.review')} trailingIcon="forward" onPress={() => go('Approvals')} />
      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>{t('manager.home.decisions.definition')}</Text>
    </Surface>
  );
}

const TILE_ORDER: TeamBucket[] = ['present', 'notIn', 'onLeave', 'missingPunch', 'absent', 'off', 'other'];
const ALWAYS_SHOWN: TeamBucket[] = ['present', 'notIn', 'onLeave'];

function TeamTodaySection({
  team,
  onRetry,
  go,
}: {
  team: Load<TeamTodayData>;
  onRetry: () => void;
  go: (route: AppRoute, params?: Record<string, unknown>) => void;
}) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const counts = useMemo(() => (team.data ? teamTodayCounts(team.data.members, team.data.day) : null), [team.data]);

  const heading = (
    <View style={styles.sectionHead}>
      <Text accessibilityRole="header" style={[theme.typography.title3, styles.flex, { color: theme.colors.text }]}>
        {t('manager.home.team.title')}
      </Text>
      {team.data ? (
        <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
          {t('manager.home.team.asOf', { time: f.time(team.data.loadedAt) })}
        </Text>
      ) : null}
    </View>
  );

  if (team.state === 'loading' && !team.data) return <>{heading}<SkeletonCard lines={2} /></>;
  if (!team.data || !counts) return <>{heading}<StateCard variant="error" title={t('manager.home.team.errorTitle')} onRetry={onRetry} /></>;
  if (counts.total === 0) {
    return <>{heading}<StateCard variant="empty" title={t('manager.home.team.emptyTitle')} body={t('manager.home.team.emptyBody')} /></>;
  }

  const holidayName = team.data.holiday
    ? (i18n.language.startsWith('ar') ? team.data.holiday.nameAr : team.data.holiday.nameEn) ?? team.data.holiday.nameEn
    : null;
  const tiles = TILE_ORDER.filter((b) => ALWAYS_SHOWN.includes(b) || counts[b] > 0);
  const period = t('manager.home.team.period');

  return (
    <>
      {heading}
      {team.data.holiday ? (
        <StatusPill
          tone="info"
          icon="flag-outline"
          label={holidayName ? t('manager.home.team.holiday', { name: holidayName }) : t('manager.home.team.holidayNoName')}
          style={styles.pillGap}
        />
      ) : null}
      <View style={styles.tiles}>
        {tiles.map((b) => (
          <KpiTile
            key={b}
            style={styles.tile}
            label={text.bucket(b)}
            value={f.number(counts[b])}
            period={period}
            tone={counts[b] > 0 && BUCKET_TONE[b] !== 'neutral' && BUCKET_TONE[b] !== 'success' ? BUCKET_TONE[b] : undefined}
            caption={b === 'present' && counts.late > 0 ? t('manager.home.team.lateCaption', { n: f.number(counts.late) }) : undefined}
            onPress={() => go('Team', { segment: 'today', filter: b })}
          />
        ))}
      </View>
      <Text style={[theme.typography.caption, styles.definition, { color: theme.colors.textMuted }]}>{t('manager.team.definition')}</Text>
      <MotionPressable
        accessibilityRole="button"
        onPress={() => go('Team', { segment: 'today' })}
        contentStyle={styles.textLink}
      >
        <Text style={[theme.typography.headline, { color: theme.colors.accent }]}>
          {t('manager.home.team.viewAll', { n: f.number(counts.total) })}
        </Text>
        <DirectionalIcon name="chevron" size={16} color={theme.colors.accent} />
      </MotionPressable>
    </>
  );
}

function WhosOutCard({ out, go }: { out: Load<OutEntry[]>; go: (route: AppRoute, params?: Record<string, unknown>) => void }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  if (out.state === 'loading' && !out.data) return <SkeletonCard lines={2} />;
  const entries = out.data ?? [];
  const people = peopleOut(entries);

  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityHint={t('kit.opensRecords')}
      onPress={() => go('Team', { segment: 'whosOut' })}
    >
      <Surface contentStyle={styles.card}>
        <View style={styles.row}>
          <Ionicons accessible={false} name="airplane-outline" size={20} color={theme.colors.accent} />
          <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>
            {t('manager.home.whosOut.title', { days: f.number(WHOS_OUT_DAYS) })}
          </Text>
          <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
        </View>
        {!out.data ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.status.warning.fg }]}>{t('manager.home.whosOut.unavailable')}</Text>
        ) : people === 0 ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('manager.home.whosOut.none')}</Text>
        ) : (
          <>
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
              {t('manager.home.whosOut.summary', { n: f.number(people) })}
            </Text>
            {entries.slice(0, 3).map((e) => (
              <Text key={`${e.employeeId}-${e.startDate}`} numberOfLines={1} style={[theme.typography.callout, { color: theme.colors.text }]}>
                {e.employeeName} · {text.dateRange(e.startDate, e.endDate)}
              </Text>
            ))}
          </>
        )}
      </Surface>
    </MotionPressable>
  );
}

function MyDayCard({
  attendance,
  punching,
  feedback,
  notes,
  onPunch,
  onHistory,
  onRetry,
}: {
  attendance: Load<TodayAttendance>;
  punching: boolean;
  feedback: { type: PunchType; selfie: boolean } | null;
  notes: ReturnType<typeof usePunchFlow>['notes'];
  onPunch: (type: PunchType) => void;
  onHistory: () => void;
  onRetry: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const a = attendance.data;

  // State from punches only — never a guessed "Absent".
  const state = !a ? 'unknown' : a.currentlyActive ? 'in' : a.clockIn && a.clockOut ? 'done' : 'notYet';
  const headline = {
    unknown: t('manager.home.myDay.unavailable'),
    in: t('manager.home.myDay.stillIn'),
    done: t('manager.home.myDay.dayComplete'),
    notYet: t('manager.home.myDay.notYet'),
  }[state];
  const times = [
    a?.clockIn ? t('manager.home.myDay.in', { time: f.time(a.clockIn) }) : null,
    a?.clockOut && !a.currentlyActive ? t('manager.home.myDay.out', { time: f.time(a.clockOut) }) : null,
    a?.shiftStart && a?.shiftEnd ? t('manager.home.myDay.shift', { from: f.time(a.shiftStart), to: f.time(a.shiftEnd) }) : null,
  ].filter(Boolean).join(' · ');

  if (attendance.state === 'loading' && !a) return <SkeletonCard lines={2} />;

  return (
    <Surface contentStyle={styles.card}>
      <Text accessibilityRole="header" style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
        {t('manager.home.myDay.title')}
      </Text>
      <Text style={[theme.typography.headline, { color: state === 'unknown' ? theme.colors.status.warning.fg : theme.colors.text }]}>
        {headline}
      </Text>
      {times ? <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{times}</Text> : null}
      {feedback ? (
        <Text accessibilityLiveRegion="polite" style={[theme.typography.footnote, { color: theme.colors.status.success.fg }]}>
          {feedback.type === 'CLOCK_IN' ? t('manager.home.myDay.punchedIn') : t('manager.home.myDay.punchedOut')}
          {feedback.selfie ? ` ${t('manager.home.myDay.selfieAttached')}` : ''}
        </Text>
      ) : null}
      {notes.geofence ? <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{notes.geofence}</Text> : null}
      {notes.selfie ? (
        <MotionPressable accessibilityRole="button" onPress={notes.selfie.onPress} contentStyle={styles.noteLink}>
          <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.textMuted }]}>{notes.selfie.text}</Text>
          <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.accent }]}>{notes.selfie.actionLabel}</Text>
        </MotionPressable>
      ) : null}
      {state === 'unknown' ? (
        <LiquidButton label={t('kit.retry')} variant="secondary" size="compact" onPress={onRetry} />
      ) : state === 'in' ? (
        <LiquidButton label={t('manager.home.myDay.clockOut')} icon="log-out-outline" variant="secondary" loading={punching} onPress={() => onPunch('CLOCK_OUT')} />
      ) : state === 'notYet' ? (
        <LiquidButton label={t('manager.home.myDay.clockIn')} icon="log-in-outline" loading={punching} onPress={() => onPunch('CLOCK_IN')} />
      ) : null}
      <MotionPressable accessibilityRole="button" onPress={onHistory} contentStyle={styles.textLink}>
        <Text style={[theme.typography.subheadline, styles.semibold, { color: theme.colors.accent }]}>{t('manager.home.myDay.history')}</Text>
        <DirectionalIcon name="chevron" size={16} color={theme.colors.accent} />
      </MotionPressable>
    </Surface>
  );
}

function LinkRow({
  icon,
  label,
  onPress,
  last,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  label: string;
  onPress: () => void;
  last?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <MotionPressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress}>
      <View style={[styles.linkRow, !last && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}>
        <Ionicons accessible={false} name={icon} size={20} color={theme.colors.accent} />
        <Text style={[theme.typography.callout, styles.flex, { color: theme.colors.text }]}>{label}</Text>
        <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
      </View>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  flex: { flex: 1 },
  content: { paddingBottom: 24 },
  section: { paddingHorizontal: 16, marginBottom: 20 },
  sectionHead: { flexDirection: 'row', alignItems: 'baseline', gap: 8, marginBottom: 10 },
  sectionTitle: { marginBottom: 10 },
  heroActions: { flexDirection: 'row', gap: 8 },
  avatarButton: { width: 44, height: 44, borderRadius: 22 },
  hero: { padding: 18, gap: 10 },
  heroCount: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  tabular: { fontVariant: ['tabular-nums'] },
  row: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  kindRow: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  kindChip: { minHeight: 36, borderRadius: 18, paddingHorizontal: 12, justifyContent: 'center' },
  semibold: { fontWeight: '600' },
  tiles: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  tile: { flexBasis: '47%', flexGrow: 1 },
  pillGap: { marginBottom: 10 },
  definition: { marginTop: 8 },
  textLink: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 4, alignSelf: 'flex-start' },
  card: { padding: 16, gap: 8 },
  notice: { flexDirection: 'row', gap: 12, padding: 14, alignItems: 'flex-start' },
  noteLink: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 8 },
  linkList: { paddingHorizontal: 14 },
  linkRow: { minHeight: 52, flexDirection: 'row', alignItems: 'center', gap: 12 },
});
