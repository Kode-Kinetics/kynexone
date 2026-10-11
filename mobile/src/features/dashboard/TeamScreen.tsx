// My team — "Today" (each direct report's status, bucketed by the same definitions Manager Home
// counts with) and "Who's out" (approved leave over the next 14 days, from the team leave calendar).

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { teamApi } from '@/api/services';
import { teamCalendarApi } from '@/api/teamCalendar';
import { useAuthStore } from '@/auth/authStore';
import { navigateTo } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import {
  EmployeeAvatar,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SegmentedControl,
  SkeletonCard,
  SkeletonRow,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import {
  addDays,
  BUCKET_TONE,
  isLate,
  teamBucket,
  teamTodayCounts,
  whosOutByDay,
  type OutEntry,
  type TeamBucket,
  type TeamDay,
  type TeamMemberToday,
} from '@/features/approvals/teamModel';
import { useManagerText } from '@/features/approvals/useManagerText';

type Segment = 'today' | 'whosOut';
const DAYS = 14;
const ALWAYS_SHOWN: TeamBucket[] = ['present', 'notIn', 'onLeave'];
const ORDER: TeamBucket[] = ['present', 'notIn', 'onLeave', 'missingPunch', 'absent', 'off', 'other'];

interface Props {
  navigation: any;
  route?: { params?: { segment?: Segment; filter?: TeamBucket } };
}

export default function TeamScreen({ navigation, route }: Props) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const [segment, setSegment] = useState<Segment>(route?.params?.segment ?? 'today');
  const [filter, setFilter] = useState<TeamBucket | 'all'>(route?.params?.filter ?? 'all');
  const [refreshing, setRefreshing] = useState(false);

  const [team, setTeam] = useState<{ members: TeamMemberToday[]; day: TeamDay; holiday: { nameEn: string | null; nameAr: string | null } | null } | null>(null);
  const [teamState, setTeamState] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [out, setOut] = useState<OutEntry[] | null>(null);
  const [outState, setOutState] = useState<'idle' | 'loading' | 'ready' | 'failed'>('idle');
  const today = f.businessDate() ?? new Date().toISOString().slice(0, 10);
  const loadedOnce = useRef(false);

  useEffect(() => {
    if (route?.params?.segment) setSegment(route.params.segment);
    setFilter(route?.params?.filter ?? 'all');
  }, [route?.params?.segment, route?.params?.filter]);

  const loadTeam = useCallback(async () => {
    try {
      setTeam(await teamApi.getTeamToday());
      setTeamState('ready');
    } catch {
      setTeamState('failed');
    } finally {
      setRefreshing(false);
    }
  }, []);

  const loadOut = useCallback(async () => {
    setOutState((s) => (s === 'ready' ? s : 'loading'));
    try {
      setOut(await teamCalendarApi.getTeamLeave(today, addDays(today, DAYS - 1)));
      setOutState('ready');
    } catch {
      setOutState('failed');
    } finally {
      setRefreshing(false);
    }
  }, [today]);

  useFocusEffect(
    useCallback(() => {
      void loadTeam();
      if (loadedOnce.current) void loadOut();
      loadedOnce.current = true;
    }, [loadOut, loadTeam]),
  );

  useEffect(() => {
    if (segment === 'whosOut' && outState === 'idle') void loadOut();
  }, [loadOut, outState, segment]);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        showsVerticalScrollIndicator={false}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void (segment === 'today' ? loadTeam() : loadOut());
            }}
            tintColor={theme.colors.accent}
          />
        }
      >
        <ScreenHero
          title={t('manager.team.title')}
          subtitle={team ? t('manager.team.subtitle', { n: f.number(team.members.length) }) : undefined}
        />
        <View style={styles.section}>
          <SegmentedControl<Segment>
            segments={[
              { key: 'today', label: t('manager.team.today') },
              { key: 'whosOut', label: t('manager.team.whosOut') },
            ]}
            value={segment}
            onChange={setSegment}
          />
        </View>
        {segment === 'today' ? (
          <TodayView team={team} state={teamState} filter={filter} onFilter={setFilter} onRetry={() => void loadTeam()} />
        ) : (
          <WhosOutView
            entries={out}
            state={outState}
            today={today}
            onRetry={() => void loadOut()}
            onOpenApprovals={() => navigateTo(navigation, 'Approvals', useAuthStore.getState().user, { filterType: 'LEAVE' })}
          />
        )}
      </ScrollView>
    </View>
  );
}

function TodayView({
  team,
  state,
  filter,
  onFilter,
  onRetry,
}: {
  team: { members: TeamMemberToday[]; day: TeamDay; holiday: { nameEn: string | null; nameAr: string | null } | null } | null;
  state: 'loading' | 'ready' | 'failed';
  filter: TeamBucket | 'all';
  onFilter: (b: TeamBucket | 'all') => void;
  onRetry: () => void;
}) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const [search, setSearch] = useState('');
  const counts = useMemo(() => (team ? teamTodayCounts(team.members, team.day) : null), [team]);

  const visible = useMemo(() => {
    if (!team) return [];
    const q = search.trim().toLowerCase();
    return team.members.filter((m) => {
      if (filter !== 'all' && teamBucket(m.rawStatus, team.day) !== filter) return false;
      if (!q) return true;
      return [m.fullName, m.jobTitle, m.department].some((v) => v?.toLowerCase().includes(q));
    });
  }, [filter, search, team]);

  if (!team && state === 'loading') {
    return (
      <View style={styles.section}>
        <SkeletonCard lines={1} />
        <SkeletonRow />
        <SkeletonRow />
        <SkeletonRow />
      </View>
    );
  }
  if (!team || !counts) {
    return (
      <View style={styles.section}>
        <StateCard variant="error" title={t('manager.team.errorTitle')} onRetry={onRetry} />
      </View>
    );
  }
  if (counts.total === 0) {
    return (
      <View style={styles.section}>
        <StateCard variant="empty" title={t('manager.team.emptyTitle')} body={t('manager.team.emptyBody')} />
      </View>
    );
  }

  const chips: (TeamBucket | 'all')[] = ['all', ...ORDER.filter((b) => ALWAYS_SHOWN.includes(b) || counts[b] > 0)];
  const holidayName = team.holiday
    ? (i18n.language.startsWith('ar') ? team.holiday.nameAr : team.holiday.nameEn) ?? team.holiday.nameEn
    : null;

  return (
    <>
      {team.holiday ? (
        <View style={styles.section}>
          <StatusPill
            tone="info"
            icon="flag-outline"
            label={holidayName ? t('manager.home.team.holiday', { name: holidayName }) : t('manager.home.team.holidayNoName')}
          />
        </View>
      ) : null}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.chipRail} contentContainerStyle={styles.chips}>
        {chips.map((b) => {
          const on = filter === b;
          const label = b === 'all' ? t('manager.team.all') : text.bucket(b);
          const n = b === 'all' ? counts.total : counts[b];
          return (
            <MotionPressable
              key={b}
              haptic="selection"
              accessibilityRole="button"
              accessibilityState={{ selected: on }}
              onPress={() => onFilter(b)}
              contentStyle={[
                styles.chip,
                { backgroundColor: on ? theme.colors.primaryFill : theme.colors.card, borderColor: on ? theme.colors.primaryFill : theme.colors.hairline },
              ]}
            >
              <Text style={[theme.typography.footnote, styles.semibold, { color: on ? theme.colors.onFill : theme.colors.textSecondary }]}>
                {t('manager.approvals.filterCount', { label, n: f.number(n) })}
              </Text>
            </MotionPressable>
          );
        })}
      </ScrollView>
      <Text style={[theme.typography.caption, styles.definition, { color: theme.colors.textMuted }]}>{t('manager.team.definition')}</Text>

      {team.members.length > 6 ? (
        <View style={styles.section}>
          <Surface level={0} contentStyle={styles.search}>
            <Ionicons accessible={false} name="search-outline" size={18} color={theme.colors.textMuted} />
            <TextInput
              value={search}
              onChangeText={setSearch}
              placeholder={t('manager.team.search')}
              accessibilityLabel={t('manager.team.search')}
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.accent}
              autoCorrect={false}
              style={[theme.typography.callout, styles.flex, styles.searchInput, { color: theme.colors.text }]}
            />
            {search ? (
              <MotionPressable
                accessibilityRole="button"
                accessibilityLabel={t('manager.team.clearSearch')}
                onPress={() => setSearch('')}
                contentStyle={styles.iconButton}
              >
                <Ionicons accessible={false} name="close-circle" size={18} color={theme.colors.textMuted} />
              </MotionPressable>
            ) : null}
          </Surface>
        </View>
      ) : null}

      <View style={styles.section}>
        {visible.length === 0 ? (
          <StateCard
            variant="empty"
            title={t('manager.team.noMatchTitle')}
            body={t('manager.team.noMatchBody')}
            action={{
              label: t('manager.team.clearFilters'),
              onPress: () => {
                setSearch('');
                onFilter('all');
              },
            }}
          />
        ) : (
          <Surface contentStyle={styles.list}>
            {visible.map((m, i) => (
              <MemberRow key={m.employeeId} member={m} day={team.day} last={i === visible.length - 1} />
            ))}
          </Surface>
        )}
      </View>
    </>
  );
}

function MemberRow({ member, day, last }: { member: TeamMemberToday; day: TeamDay; last: boolean }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const bucket = teamBucket(member.rawStatus, day);
  const label = isLate(member.rawStatus)
    ? t('manager.status.late')
    : /half/i.test(member.rawStatus)
      ? t('manager.status.halfDay')
      : bucket === 'other'
        ? member.rawStatus
        : text.bucket(bucket);
  const tone = isLate(member.rawStatus) ? 'warning' : BUCKET_TONE[bucket];
  const times = [
    member.clockIn ? t('manager.team.in', { time: f.time(member.clockIn) }) : null,
    member.clockOut && !member.currentlyActive
      ? t('manager.team.out', { time: f.time(member.clockOut) })
      : member.currentlyActive
        ? t('manager.team.stillIn')
        : null,
  ].filter(Boolean).join(' · ');

  return (
    <View
      accessible
      accessibilityLabel={[member.fullName, member.jobTitle, label, times].filter(Boolean).join(', ')}
      style={[styles.member, !last && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}
    >
      <EmployeeAvatar name={member.fullName} photoUrl={member.profilePhotoUrl} size={40} />
      <View style={styles.flex}>
        <Text numberOfLines={1} style={[theme.typography.headline, { color: theme.colors.text }]}>{member.fullName}</Text>
        {member.jobTitle ? (
          <Text numberOfLines={1} style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{member.jobTitle}</Text>
        ) : null}
        {times ? <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{times}</Text> : null}
      </View>
      <StatusPill tone={tone} label={label} />
    </View>
  );
}

function WhosOutView({
  entries,
  state,
  today,
  onRetry,
  onOpenApprovals,
}: {
  entries: OutEntry[] | null;
  state: 'idle' | 'loading' | 'ready' | 'failed';
  today: string;
  onRetry: () => void;
  onOpenApprovals: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const [selectedDay, setSelectedDay] = useState(today);
  const days = useMemo(() => whosOutByDay(entries ?? [], today, DAYS), [entries, today]);
  const day = days.find((d) => d.date === selectedDay) ?? days[0];

  if (!entries && (state === 'loading' || state === 'idle')) {
    return (
      <View style={styles.section}>
        <SkeletonCard lines={1} />
        <SkeletonRow />
        <SkeletonRow />
      </View>
    );
  }
  if (!entries) {
    return (
      <View style={styles.section}>
        <StateCard variant="error" title={t('manager.team.out14.errorTitle')} onRetry={onRetry} />
      </View>
    );
  }

  return (
    <>
      <Text style={[theme.typography.caption, styles.definition, { color: theme.colors.textMuted }]}>{t('manager.team.out14.definition')}</Text>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.chipRail} contentContainerStyle={styles.chips}>
        {days.map((d) => {
          const on = d.date === day?.date;
          const count = d.out.length;
          return (
            <MotionPressable
              key={d.date}
              haptic="selection"
              accessibilityRole="button"
              accessibilityState={{ selected: on }}
              accessibilityLabel={t('manager.team.out14.dayA11y', { date: f.date(d.date, 'full'), n: f.number(count) })}
              onPress={() => setSelectedDay(d.date)}
              contentStyle={[
                styles.dayCell,
                { backgroundColor: on ? theme.colors.primaryFill : theme.colors.card, borderColor: on ? theme.colors.primaryFill : theme.colors.hairline },
              ]}
            >
              <Text style={[theme.typography.caption, { color: on ? theme.colors.onFill : theme.colors.textSecondary }]}>{f.date(d.date, 'weekday')}</Text>
              <Text style={[theme.typography.footnote, styles.semibold, { color: on ? theme.colors.onFill : theme.colors.text }]}>{f.date(d.date, 'dayMonth')}</Text>
              <View
                style={[
                  styles.dot,
                  { backgroundColor: count ? (on ? theme.colors.onFill : theme.colors.status.warning.fg) : 'transparent' },
                ]}
              />
            </MotionPressable>
          );
        })}
      </ScrollView>

      {day ? (
        <View style={styles.section}>
          <Text accessibilityRole="header" style={[theme.typography.headline, styles.subhead, { color: theme.colors.text }]}>
            {t('manager.team.out14.outOn', { date: f.date(day.date, 'full') })}
          </Text>
          {day.out.length === 0 ? (
            <Surface level={0} contentStyle={styles.card}>
              <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
                {t('manager.team.out14.dayEmpty', { date: f.date(day.date, 'dayMonth') })}
              </Text>
            </Surface>
          ) : (
            <Surface contentStyle={styles.list}>
              {day.out.map((e, i) => (
                <OutRow key={`${e.employeeId}-${e.startDate}`} entry={e} last={i === day.out.length - 1} />
              ))}
            </Surface>
          )}
        </View>
      ) : null}

      <View style={styles.section}>
        <Text accessibilityRole="header" style={[theme.typography.headline, styles.subhead, { color: theme.colors.text }]}>
          {t('manager.team.out14.upcoming', { days: f.number(DAYS) })}
        </Text>
        {entries.length === 0 ? (
          <Surface level={0} contentStyle={styles.card}>
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
              {t('manager.team.out14.upcomingEmpty', { days: f.number(DAYS) })}
            </Text>
          </Surface>
        ) : (
          <Surface contentStyle={styles.list}>
            {entries.map((e, i) => (
              <OutRow key={`${e.employeeId}-${e.startDate}-all`} entry={e} last={i === entries.length - 1} showRange />
            ))}
          </Surface>
        )}
        <MotionPressable accessibilityRole="button" onPress={onOpenApprovals} contentStyle={styles.textLink}>
          <Text style={[theme.typography.subheadline, styles.semibold, { color: theme.colors.accent }]}>{t('manager.team.out14.openApprovals')}</Text>
          <Ionicons accessible={false} name="checkmark-done-outline" size={16} color={theme.colors.accent} />
        </MotionPressable>
      </View>
    </>
  );

}

function OutRow({ entry, last, showRange }: { entry: OutEntry; last: boolean; showRange?: boolean }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const text = useManagerText();
  const range = text.dateRange(entry.startDate, entry.endDate);
  const type = entry.leaveTypeName ?? t('manager.team.out14.leave');
  return (
    <View
      accessible
      style={[styles.member, !last && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}
    >
      <EmployeeAvatar name={entry.employeeName} size={36} />
      <View style={styles.flex}>
        <Text numberOfLines={1} style={[theme.typography.headline, { color: theme.colors.text }]}>{entry.employeeName}</Text>
        <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
          {showRange
            ? t('manager.team.out14.leaveRange', { type, dates: range })
            : [type, entry.days !== null ? text.days(entry.days) : null].filter(Boolean).join(' · ')}
        </Text>
      </View>
      {!entry.approved ? <StatusPill tone="warning" label={t('manager.approvals.sheet.notApprovedYet')} /> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  flex: { flex: 1 },
  content: { paddingBottom: 24 },
  section: { paddingHorizontal: 16, marginBottom: 14, gap: 8 },
  chipRail: { flexGrow: 0, marginBottom: 6 },
  chips: { paddingHorizontal: 16, gap: 8 },
  chip: { minHeight: 44, borderRadius: 22, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, justifyContent: 'center' },
  semibold: { fontWeight: '600' },
  definition: { paddingHorizontal: 20, marginBottom: 12 },
  search: { flexDirection: 'row', alignItems: 'center', gap: 8, paddingHorizontal: 12, minHeight: 48 },
  searchInput: { minHeight: 44, textAlign: 'auto' },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  list: { paddingHorizontal: 14 },
  member: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 12, minHeight: 64 },
  card: { padding: 14 },
  subhead: { marginBottom: 2 },
  dayCell: { width: 64, minHeight: 72, borderRadius: 14, borderWidth: StyleSheet.hairlineWidth, alignItems: 'center', justifyContent: 'center', gap: 2 },
  dot: { width: 6, height: 6, borderRadius: 3, marginTop: 2 },
  textLink: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 6, alignSelf: 'flex-start' },
});
