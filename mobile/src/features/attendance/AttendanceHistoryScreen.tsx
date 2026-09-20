import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { parseISO } from 'date-fns';
import { attendanceApi } from '@/api/services';
import { formatTime, formatWorkHours } from '@/utils/date';
import { navigateTo, isManagerUser } from '@/navigation/routes';
import { useAuthStore } from '@/auth/authStore';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { AttendanceDay } from '@/types';

interface Props {
  navigation: any;
}

export default function AttendanceHistoryScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const now = new Date();
  const [year, setYear] = useState(now.getFullYear());
  const [month, setMonth] = useState(now.getMonth() + 1);
  const [records, setRecords] = useState<AttendanceDay[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async (isRefresh = false) => {
    if (isRefresh) setRefreshing(true);
    else setLoading(true);
    setError(null);
    try {
      setRecords(await attendanceApi.getMonthlyAttendance(year, month));
    } catch (requestError: any) {
      console.error('[AttendanceHistory]', requestError);
      setRecords([]);
      setError(requestError?.message ?? 'Could not load attendance.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [month, year]);

  useEffect(() => {
    void load();
  }, [load]);

  const previousMonth = () => {
    if (month === 1) {
      setYear((current) => current - 1);
      setMonth(12);
    } else {
      setMonth((current) => current - 1);
    }
  };

  const nextMonth = () => {
    const current = new Date();
    if (year === current.getFullYear() && month === current.getMonth() + 1) return;
    if (month === 12) {
      setYear((value) => value + 1);
      setMonth(1);
    } else {
      setMonth((value) => value + 1);
    }
  };

  const monthLabel = new Date(year, month - 1, 1).toLocaleDateString('en-US', {
    month: 'long',
    year: 'numeric',
  });
  const summary = useMemo(() => records.reduce(
    (accumulator, record) => {
      if (record.status === 'PRESENT') accumulator.present += 1;
      else if (record.status === 'ABSENT') accumulator.absent += 1;
      else if (record.status === 'LATE') {
        accumulator.present += 1;
        accumulator.late += 1;
      } else if (record.status === 'ON_LEAVE') accumulator.onLeave += 1;
      accumulator.totalHours += record.workHours ?? 0;
      return accumulator;
    },
    { present: 0, absent: 0, late: 0, onLeave: 0, totalHours: 0 },
  ), [records]);

  const openCorrection = (date?: string) => {
    navigateTo(
      navigation,
      'AttendanceCorrection',
      isManagerUser(user),
      date ? { date } : undefined,
    );
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <FlatList
        data={!loading && !error ? records : []}
        keyExtractor={(item) => item.date}
        renderItem={({ item }) => (
          <View style={styles.recordItem}>
            <AttendanceRecordCard
              record={item}
              onCorrection={() => openCorrection(item.date)}
            />
          </View>
        )}
        ItemSeparatorComponent={() => <View style={styles.recordGap} />}
        contentContainerStyle={styles.content}
        refreshing={refreshing}
        onRefresh={() => void load(true)}
        showsVerticalScrollIndicator={false}
        ListHeaderComponent={
          <>
            <ScreenHero
              eyebrow="Time & attendance"
              title="Attendance history"
              subtitle="Monthly punches, status and exceptions"
              actions={
                <View style={styles.heroActions}>
                  {navigation.canGoBack() ? (
                    <GlassIconButton icon="arrow-back" label="Go back" onPress={() => navigation.goBack()} />
                  ) : null}
                  <GlassIconButton
                    icon="create-outline"
                    label="Request attendance correction"
                    accent
                    onPress={() => openCorrection()}
                  />
                </View>
              }
            />
            <View style={styles.section}>
              <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.monthPicker}>
                <MotionPressable
                  onPress={previousMonth}
                  haptic="selection"
                  contentStyle={styles.monthArrow}
                  accessibilityLabel="Previous month"
                >
                  <Ionicons name="chevron-back" size={20} color={theme.colors.text} />
                </MotionPressable>
                <View style={styles.monthCopy}>
                  <Text style={[theme.typography.h3, { color: theme.colors.text }]}>{monthLabel}</Text>
                  <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>
                    {records.length} recorded day{records.length === 1 ? '' : 's'}
                  </Text>
                </View>
                <MotionPressable
                  onPress={nextMonth}
                  haptic="selection"
                  contentStyle={styles.monthArrow}
                  accessibilityLabel="Next month"
                >
                  <Ionicons name="chevron-forward" size={20} color={theme.colors.text} />
                </MotionPressable>
              </GlassSurface>
            </View>

            <View style={styles.section}>
              <SectionHeader title="Month summary" subtitle="Your attendance at a glance" />
              <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.summaryRail}>
                <SummaryChip label="Present" value={summary.present} accent={theme.colors.success} icon="checkmark-circle-outline" />
                <SummaryChip label="Absent" value={summary.absent} accent={theme.colors.danger} icon="close-circle-outline" />
                <SummaryChip label="Late" value={summary.late} accent={theme.colors.warning} icon="time-outline" />
                <SummaryChip label="Leave" value={summary.onLeave} accent={theme.colors.primary} icon="airplane-outline" />
                <SummaryChip label="Hours" value={formatWorkHours(summary.totalHours)} accent={theme.colors.violet} icon="hourglass-outline" />
              </ScrollView>
            </View>

            <View style={styles.section}>
              <SectionHeader title="Daily record" subtitle="Punches and exceptions" />
            </View>
          </>
        }
        ListEmptyComponent={
          <View style={styles.section}>
            {loading ? (
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
                <ActivityIndicator color={theme.colors.primary} />
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading attendance…</Text>
              </GlassSurface>
            ) : error ? (
              <StateCard
                icon="alert-circle-outline"
                title="Attendance unavailable"
                subtitle={error}
                accent={theme.colors.danger}
                actionLabel="Try again"
                onAction={() => void load()}
              />
            ) : (
              <StateCard
                icon="calendar-outline"
                title="No attendance recorded"
                subtitle="There are no attendance records for this month."
                accent={theme.colors.textMuted}
              />
            )}
          </View>
        }
        ListFooterComponent={<View style={styles.bottomSpacer} />}
      />
    </View>
  );

}
function SummaryChip({
  label,
  value,
  accent,
  icon,
}: {
  label: string;
  value: string | number;
  accent: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
}) {
  const { theme } = useTheme();
  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.summaryCard} contentStyle={styles.summaryContent}>
      <View style={[styles.summaryIcon, { backgroundColor: `${accent}19` }]}>
        <Ionicons name={icon} size={18} color={accent} />
      </View>
      <Text style={[styles.summaryValue, { color: theme.colors.text }]}>{value}</Text>
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{label}</Text>
    </GlassSurface>
  );
}

function AttendanceRecordCard({
  record,
  onCorrection,
}: {
  record: AttendanceDay;
  onCorrection: () => void;
}) {
  const { theme } = useTheme();
  const config = getStatusConfig(record.status, theme);
  const date = parseISO(record.date);

  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.recordCard}>
      <View style={[styles.dateTile, { backgroundColor: `${config.color}12` }]}>
        <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
          {date.toLocaleDateString('en-US', { weekday: 'short' })}
        </Text>
        <Text style={[styles.recordDate, { color: theme.colors.text }]}>{date.getDate()}</Text>
      </View>

      <View style={styles.recordMain}>
        <View style={styles.recordTop}>
          <View style={[styles.statusPill, { backgroundColor: config.background }]}>
            <View style={[styles.statusDot, { backgroundColor: config.color }]} />
            <Text style={[theme.typography.micro, { color: config.color }]}>{config.label}</Text>
          </View>
          {record.shiftName ? (
            <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{record.shiftName}</Text>
          ) : null}
          {(record.lateMinutes ?? 0) > 0 ? (
            <View style={[styles.latePill, { backgroundColor: `${theme.colors.warning}16` }]}>
              <Ionicons name="warning-outline" size={12} color={theme.colors.warning} />
              <Text style={[theme.typography.micro, { color: theme.colors.warning }]}>
                {record.lateMinutes}m late
              </Text>
            </View>
          ) : null}
        </View>
        <View style={styles.timeRow}>
          <TimeEntry icon="log-in-outline" time={record.clockIn} accent={theme.colors.success} />
          <Ionicons name="remove-outline" size={14} color={theme.colors.textMuted} />
          <TimeEntry icon="log-out-outline" time={record.clockOut} accent={theme.colors.danger} />
          {(record.workHours ?? 0) > 0 ? (
            <>
              <View style={[styles.timeDot, { backgroundColor: theme.colors.textMuted }]} />
              <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
                {formatWorkHours(record.workHours!)}
              </Text>
            </>
          ) : null}
        </View>
      </View>

      {record.status === 'MISSING_PUNCH' ? (
        <MotionPressable
          onPress={onCorrection}
          haptic="selection"
          contentStyle={[styles.correctionButton, { backgroundColor: `${theme.colors.primary}18` }]}
          accessibilityLabel={`Request correction for ${record.date}`}
        >
          <Ionicons name="create-outline" size={17} color={theme.colors.primary} />
        </MotionPressable>
      ) : null}
    </GlassSurface>
  );
}

function TimeEntry({
  icon,
  time,
  accent,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  time?: string;
  accent: string;
}) {
  const { theme } = useTheme();
  return (
    <View style={styles.timeEntry}>
      <Ionicons name={icon} size={13} color={accent} />
      <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
        {time ? formatTime(time) : '—'}
      </Text>
    </View>
  );
}

function StateCard({
  icon,
  title,
  subtitle,
  accent,
  actionLabel,
  onAction,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  title: string;
  subtitle: string;
  accent: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  const { theme } = useTheme();
  return (
    <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
      <View style={[styles.stateIcon, { backgroundColor: `${accent}18` }]}>
        <Ionicons name={icon} size={27} color={accent} />
      </View>
      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>{title}</Text>
      <Text style={[theme.typography.caption, styles.stateSubtitle, { color: theme.colors.textMuted }]}>
        {subtitle}
      </Text>
      {actionLabel && onAction ? (
        <MotionPressable onPress={onAction} haptic="selection" contentStyle={styles.stateAction}>
          <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
            {actionLabel}
          </Text>
        </MotionPressable>
      ) : null}
    </GlassSurface>
  );
}
function getStatusConfig(status: string, theme: ReturnType<typeof useTheme>['theme']) {
  const map: Record<string, { color: string; background: string; label: string }> = {
    PRESENT: { color: theme.colors.success, background: `${theme.colors.success}18`, label: 'Present' },
    ABSENT: { color: theme.colors.danger, background: `${theme.colors.danger}18`, label: 'Absent' },
    LATE: { color: theme.colors.warning, background: `${theme.colors.warning}18`, label: 'Late' },
    HALF_DAY: { color: theme.colors.warning, background: `${theme.colors.warning}18`, label: 'Half day' },
    ON_LEAVE: { color: theme.colors.primary, background: `${theme.colors.primary}18`, label: 'On leave' },
    HOLIDAY: { color: theme.colors.violet, background: `${theme.colors.violet}18`, label: 'Holiday' },
    WEEKEND: { color: theme.colors.textMuted, background: theme.colors.surfaceSoft, label: 'Weekend' },
    MISSING_PUNCH: { color: theme.colors.danger, background: `${theme.colors.danger}18`, label: 'Missing punch' },
  };
  return map[status] ?? map.ABSENT;
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 36 },
  heroActions: { flexDirection: 'row', gap: 8 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  recordItem: { paddingHorizontal: 16 },
  recordGap: { height: 9 },
  monthPicker: {
    minHeight: 74,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
  },
  monthArrow: { width: 46, height: 46, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  monthCopy: { alignItems: 'center', flex: 1 },
  summaryRail: { gap: 10, paddingRight: 4 },
  summaryCard: { width: 112, minHeight: 128 },
  summaryContent: { padding: 14, justifyContent: 'space-between' },
  summaryIcon: { width: 38, height: 38, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  summaryValue: { fontSize: 24, lineHeight: 29, fontWeight: '800', letterSpacing: -0.45, marginTop: 8 },
  recordsList: { gap: 9 },
  recordCard: { minHeight: 92, flexDirection: 'row', alignItems: 'center', gap: 12, padding: 13 },
  dateTile: { width: 50, height: 59, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  recordDate: { fontSize: 21, lineHeight: 25, fontWeight: '800', marginTop: 2 },
  recordMain: { flex: 1, minWidth: 0 },
  recordTop: { flexDirection: 'row', alignItems: 'center', gap: 7, flexWrap: 'wrap', marginBottom: 8 },
  statusPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 8, paddingVertical: 4, borderRadius: 999 },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  latePill: { flexDirection: 'row', alignItems: 'center', gap: 3, paddingHorizontal: 7, paddingVertical: 3, borderRadius: 999 },
  timeRow: { flexDirection: 'row', alignItems: 'center', gap: 6, flexWrap: 'wrap' },
  timeEntry: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  timeDot: { width: 3, height: 3, borderRadius: 2 },
  correctionButton: { width: 38, height: 38, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  stateCard: { minHeight: 190, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  stateIcon: { width: 54, height: 54, borderRadius: 19, alignItems: 'center', justifyContent: 'center', marginBottom: 3 },
  stateSubtitle: { textAlign: 'center', maxWidth: 280 },
  stateAction: { paddingHorizontal: 14, paddingVertical: 9, borderRadius: 12, marginTop: 4 },
  bottomSpacer: { height: 12 },
});
