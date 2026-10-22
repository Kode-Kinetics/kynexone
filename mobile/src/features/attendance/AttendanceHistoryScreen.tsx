// Attendance history: a month grid with one status dot per day, then the days that need a look
// (missing punches, absences, late days) above every recorded day. Tap a day for its details and
// a labelled "Fix punch". Times are on the company clock (useFormat), not the phone's.

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { attendanceApi } from '@/api/services';
import { navigateTo } from '@/navigation/routes';
import { useAuthStore } from '@/auth/authStore';
import { deriveMobileAccess } from '@/auth/accessPolicy';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat, type UseFormat } from '@/utils/format';
import {
  BottomSheet,
  DirectionalIcon,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  Skeleton,
  SkeletonRow,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import type { AttendanceDay, AttendanceStatus } from '@/types';
import { splitMinutes } from '@/features/dashboard/employeeHomeModel';
import { STATUS_TONE, canFixDay, exceptionsFirst, monthGrid, summarizeMonth } from './attendanceHistoryModel';

interface Props {
  navigation: any;
}

type StatusKey =
  | 'home.status.PRESENT' | 'home.status.LATE' | 'home.status.ABSENT' | 'home.status.HALF_DAY'
  | 'home.status.ON_LEAVE' | 'home.status.HOLIDAY' | 'home.status.WEEKEND' | 'home.status.MISSING_PUNCH'
  | 'home.status.NOT_RECORDED';

const STATUS_KEY: Record<AttendanceStatus, StatusKey> = {
  PRESENT: 'home.status.PRESENT',
  LATE: 'home.status.LATE',
  ABSENT: 'home.status.ABSENT',
  HALF_DAY: 'home.status.HALF_DAY',
  ON_LEAVE: 'home.status.ON_LEAVE',
  HOLIDAY: 'home.status.HOLIDAY',
  WEEKEND: 'home.status.WEEKEND',
  MISSING_PUNCH: 'home.status.MISSING_PUNCH',
  UNKNOWN: 'home.status.NOT_RECORDED',
};

// A Sunday-to-Saturday week used only to print weekday names in the UI language.
const WEEK_SAMPLE = ['2026-10-04', '2026-10-05', '2026-10-06', '2026-10-07', '2026-10-08', '2026-10-09', '2026-10-10'];

export default function AttendanceHistoryScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const today = f.businessDate() ?? '';
  const [nowYear, nowMonth] = today.split('-').map(Number);
  const [year, setYear] = useState(nowYear);
  const [month, setMonth] = useState(nowMonth);
  const [records, setRecords] = useState<AttendanceDay[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const canCorrect = deriveMobileAccess(user).surfaces.has('attendanceCorrection');

  const load = useCallback(async (silent = false) => {
    if (!silent) setRecords(null);
    setFailed(false);
    try {
      setRecords(await attendanceApi.getMonthlyAttendance(year, month));
    } catch (error) {
      console.warn('[AttendanceHistory] Load failed:', error);
      setFailed(true);
    } finally {
      setRefreshing(false);
    }
  }, [month, year]);

  useEffect(() => {
    void load();
  }, [load]);

  // Back from a correction: refresh quietly so the "correction requested" state shows.
  useFocusEffect(useCallback(() => {
    void load(true);
  }, [load]));

  const isCurrentMonth = year === nowYear && month === nowMonth;
  const shiftMonth = (delta: number) => {
    const index = year * 12 + (month - 1) + delta;
    const nextYear = Math.floor(index / 12);
    const nextMonth = (index % 12) + 1;
    if (nextYear * 12 + nextMonth > nowYear * 12 + nowMonth) return;
    setYear(nextYear);
    setMonth(nextMonth);
  };

  const byDate = useMemo(() => new Map((records ?? []).map((r) => [r.date, r])), [records]);
  const weeks = useMemo(() => monthGrid(year, month), [year, month]);
  const summary = useMemo(() => summarizeMonth(records ?? []), [records]);
  const exceptions = useMemo(() => exceptionsFirst(records ?? []), [records]);
  const monthLabel = f.date(`${year}-${String(month).padStart(2, '0')}-01`, 'monthYear');
  const selectedRecord = selected ? byDate.get(selected) ?? null : null;

  const duration = (minutes: number) => {
    const { h, m } = splitMinutes(minutes);
    if (h === 0) return t('home.duration.m', { m: f.number(m) });
    if (m === 0) return t('home.duration.h', { h: f.number(h) });
    return t('home.duration.hm', { h: f.number(h), m: f.number(m) });
  };

  const openCorrection = (date: string) => {
    setSelected(null);
    navigateTo(navigation, 'AttendanceCorrection', user, { date });
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        showsVerticalScrollIndicator={false}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void load(true);
            }}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
      >
        <ScreenHero eyebrow={t('home.history.eyebrow')} title={t('home.history.title')} subtitle={t('home.history.subtitle')} />

        <View style={styles.stack}>
          {/* Month switcher + grid */}
          <View style={styles.section}>
            <Surface contentStyle={styles.calendarCard}>
              <View style={styles.monthRow}>
                <MotionPressable
                  onPress={() => shiftMonth(-1)}
                  haptic="selection"
                  accessibilityRole="button"
                  accessibilityLabel={t('home.history.previousMonth')}
                  contentStyle={styles.monthArrow}
                >
                  <DirectionalIcon name="chevronBack" size={22} color={theme.colors.accent} />
                </MotionPressable>
                <Text accessibilityRole="header" style={[theme.typography.headline, styles.monthLabel, { color: theme.colors.text }]}>
                  {monthLabel}
                </Text>
                <MotionPressable
                  onPress={() => shiftMonth(1)}
                  disabled={isCurrentMonth}
                  haptic="selection"
                  accessibilityRole="button"
                  accessibilityLabel={t('home.history.nextMonth')}
                  contentStyle={styles.monthArrow}
                >
                  <DirectionalIcon name="chevron" size={22} color={isCurrentMonth ? theme.colors.textMuted : theme.colors.accent} />
                </MotionPressable>
              </View>

              <View style={styles.weekRow} importantForAccessibility="no-hide-descendants">
                {WEEK_SAMPLE.map((d) => (
                  <Text key={d} style={[theme.typography.footnote, styles.weekday, { color: theme.colors.textMuted }]}>
                    {f.date(d, 'weekday')}
                  </Text>
                ))}
              </View>

              {weeks.map((week, wi) => (
                <View key={wi} style={styles.weekRow}>
                  {week.map((d, di) => (
                    <DayCell
                      key={d ?? `pad-${wi}-${di}`}
                      day={d}
                      record={d ? byDate.get(d) : undefined}
                      today={today}
                      loading={records === null && !failed}
                      f={f}
                      onPress={(value) => setSelected(value)}
                    />
                  ))}
                </View>
              ))}
            </Surface>
          </View>

          {failed ? (
            <View style={styles.section}>
              <StateCard
                variant="error"
                title={t('home.history.loadErrorTitle')}
                body={t('home.history.loadErrorBody')}
                onRetry={() => void load()}
              />
            </View>
          ) : records === null ? (
            <View style={styles.section}>
              <Surface contentStyle={styles.listCard}>
                <SkeletonRow />
                <SkeletonRow />
                <SkeletonRow />
              </Surface>
            </View>
          ) : records.length === 0 ? (
            <View style={styles.section}>
              <StateCard variant="empty" title={t('home.history.noRecordsTitle')} body={t('home.history.noRecordsBody')} />
            </View>
          ) : (
            <>
              {/* Summary for the month */}
              <View style={styles.section}>
                <SectionHeader title={monthLabel} />
                <Surface contentStyle={styles.summaryRow}>
                  <Stat label={t('home.history.presentDays')} value={f.number(summary.present)} />
                  <Stat label={t('home.history.lateDays')} value={f.number(summary.late)} tone={summary.late ? 'warning' : undefined} />
                  <Stat label={t('home.history.absentDays')} value={f.number(summary.absentOrMissing)} tone={summary.absentOrMissing ? 'danger' : undefined} />
                  <Stat label={t('home.history.hoursWorked')} value={f.number(Math.round(summary.workedMinutes / 6) / 10)} />
                </Surface>
              </View>

              {exceptions.length > 0 ? (
                <View style={styles.section}>
                  <SectionHeader title={t('home.history.exceptions')} subtitle={t('home.history.exceptionsHint')} />
                  <Surface contentStyle={styles.listCard}>
                    {exceptions.map((r, i) => (
                      <DayRow
                        key={r.date}
                        record={r}
                        first={i === 0}
                        f={f}
                        duration={duration}
                        onPress={() => setSelected(r.date)}
                        onFix={canCorrect && r.status === 'MISSING_PUNCH' && canFixDay(r.date, today, r) ? () => openCorrection(r.date) : undefined}
                      />
                    ))}
                  </Surface>
                </View>
              ) : null}

              <View style={styles.section}>
                <SectionHeader title={t('home.history.allDays')} />
                <Surface contentStyle={styles.listCard}>
                  {records.map((r, i) => (
                    <DayRow key={r.date} record={r} first={i === 0} f={f} duration={duration} onPress={() => setSelected(r.date)} />
                  ))}
                </Surface>
              </View>
            </>
          )}
        </View>
      </ScrollView>

      <BottomSheet
        visible={selected !== null}
        onClose={() => setSelected(null)}
        title={selected ? f.date(selected, 'full') : undefined}
        subtitle={selected ? f.hijri(selected) || undefined : undefined}
        actions={
          selected && canCorrect && canFixDay(selected, today, selectedRecord) ? (
            <LiquidButton
              label={selectedRecord?.status === 'MISSING_PUNCH' ? t('home.history.fixPunch') : t('home.history.requestCorrection')}
              variant={selectedRecord?.status === 'MISSING_PUNCH' ? 'primary' : 'secondary'}
              trailingIcon="forward"
              onPress={() => openCorrection(selected)}
            />
          ) : undefined
        }
      >
        {selectedRecord ? (
          <View style={styles.sheetBody}>
            <StatusPill tone={STATUS_TONE[selectedRecord.status]} label={t(STATUS_KEY[selectedRecord.status])} />
            <Surface level={0} contentStyle={styles.detailGrid}>
              <Detail label={t('home.history.clockIn')} value={selectedRecord.clockIn ? f.time(selectedRecord.clockIn) : '—'} />
              <Detail label={t('home.history.clockOut')} value={selectedRecord.clockOut ? f.time(selectedRecord.clockOut) : '—'} />
              <Detail
                label={t('home.history.worked')}
                value={(selectedRecord.workHours ?? 0) > 0 ? duration(Math.round((selectedRecord.workHours ?? 0) * 60)) : '—'}
              />
            </Surface>
            {(selectedRecord.lateMinutes ?? 0) > 0 ? (
              <Text style={[theme.typography.subheadline, { color: theme.colors.status.warning.fg }]}>
                {t('home.history.lateBy', { duration: duration(selectedRecord.lateMinutes ?? 0) })}
              </Text>
            ) : null}
            {(selectedRecord.earlyExitMinutes ?? 0) > 0 ? (
              <Text style={[theme.typography.subheadline, { color: theme.colors.status.warning.fg }]}>
                {t('home.history.earlyBy', { duration: duration(selectedRecord.earlyExitMinutes ?? 0) })}
              </Text>
            ) : null}
            {selectedRecord.hasCorrection ? (
              <StatusPill tone="info" icon="hourglass-outline" label={t('home.history.correctionRequested')} />
            ) : null}
          </View>
        ) : (
          <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
            {selected ? t('home.history.dayNoRecordA11y', { date: f.date(selected, 'medium') }) : ''}
          </Text>
        )}
      </BottomSheet>
    </View>
  );
}

function DayCell({
  day,
  record,
  today,
  loading,
  f,
  onPress,
}: {
  day: string | null;
  record?: AttendanceDay;
  today: string;
  loading: boolean;
  f: UseFormat;
  onPress: (day: string) => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  if (!day) return <View style={styles.cell} />;
  const future = day > today;
  const isToday = day === today;
  const tone = record ? theme.colors.status[STATUS_TONE[record.status]] : null;
  const label = record
    ? t('home.history.dayA11y', { date: f.date(day, 'long'), status: t(STATUS_KEY[record.status]) })
    : t('home.history.dayNoRecordA11y', { date: f.date(day, 'long') });

  return (
    <MotionPressable
      style={styles.cell}
      disabled={future}
      onPress={() => onPress(day)}
      accessibilityRole="button"
      accessibilityLabel={isToday ? `${t('home.history.today')}, ${label}` : label}
      contentStyle={[
        styles.cellBody,
        isToday && { borderColor: theme.colors.accent, borderWidth: 1.5 },
      ]}
    >
      <Text
        style={[
          theme.typography.subheadline,
          { color: future ? theme.colors.textMuted : theme.colors.text, fontVariant: ['tabular-nums'] },
          isToday && styles.strong,
        ]}
      >
        {f.number(Number(day.slice(8, 10)))}
      </Text>
      {loading && !future ? (
        <Skeleton width={6} height={6} radius={3} />
      ) : (
        <View style={[styles.dot, { backgroundColor: tone && record?.status !== 'WEEKEND' ? tone.fg : 'transparent' }]} />
      )}
    </MotionPressable>
  );
}

function DayRow({
  record,
  first,
  f,
  duration,
  onPress,
  onFix,
}: {
  record: AttendanceDay;
  first: boolean;
  f: UseFormat;
  duration: (minutes: number) => string;
  onPress: () => void;
  onFix?: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const times = record.clockIn || record.clockOut
    ? `${record.clockIn ? f.time(record.clockIn) : '—'} – ${record.clockOut ? f.time(record.clockOut) : '—'}`
    : null;
  const worked = (record.workHours ?? 0) > 0 ? duration(Math.round((record.workHours ?? 0) * 60)) : null;

  return (
    <View style={[styles.dayRow, !first && { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: theme.colors.hairline }]}>
      <MotionPressable
        onPress={onPress}
        accessibilityRole="button"
        accessibilityLabel={[f.date(record.date, 'full'), t(STATUS_KEY[record.status]), times, worked].filter(Boolean).join(', ')}
        style={styles.flex}
        contentStyle={styles.dayRowMain}
      >
        <View style={styles.dateTile}>
          <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{f.date(record.date, 'weekday')}</Text>
          <Text style={[theme.typography.title3, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>
            {f.number(Number(record.date.slice(8, 10)))}
          </Text>
        </View>
        <View style={styles.flex}>
          <StatusPill tone={STATUS_TONE[record.status]} label={t(STATUS_KEY[record.status])} />
          {times || worked ? (
            <Text style={[theme.typography.footnote, styles.rowMeta, { color: theme.colors.textSecondary, fontVariant: ['tabular-nums'] }]}>
              {[times, worked].filter(Boolean).join(' · ')}
            </Text>
          ) : null}
        </View>
        {!onFix ? <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} /> : null}
      </MotionPressable>
      {onFix ? (
        <LiquidButton label={t('home.history.fixPunch')} variant="secondary" size="compact" onPress={onFix} />
      ) : null}
    </View>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone?: 'warning' | 'danger' }) {
  const { theme } = useTheme();
  return (
    <View style={styles.stat} accessible accessibilityLabel={`${label}: ${value}`}>
      <Text style={[theme.typography.title2, { color: tone ? theme.colors.status[tone].fg : theme.colors.text, fontVariant: ['tabular-nums'] }]}>
        {value}
      </Text>
      <Text numberOfLines={2} style={[theme.typography.footnote, styles.center, { color: theme.colors.textSecondary }]}>{label}</Text>
    </View>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.detail} accessible accessibilityLabel={`${label}: ${value}`}>
      <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{label}</Text>
      <Text style={[theme.typography.headline, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 24 },
  stack: { gap: 24 },
  section: { paddingHorizontal: 16 },
  flex: { flex: 1, minWidth: 0 },
  strong: { fontWeight: '700' },
  center: { textAlign: 'center' },
  calendarCard: { padding: 12, gap: 4 },
  monthRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 6 },
  monthArrow: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  monthLabel: { flex: 1, textAlign: 'center' },
  weekRow: { flexDirection: 'row' },
  weekday: { flex: 1, textAlign: 'center', paddingVertical: 4 },
  cell: { flex: 1, aspectRatio: 1, maxHeight: 52, minHeight: 44 },
  cellBody: { flex: 1, margin: 2, borderRadius: 12, alignItems: 'center', justifyContent: 'center', gap: 3 },
  dot: { width: 6, height: 6, borderRadius: 3 },
  summaryRow: { flexDirection: 'row', paddingVertical: 14, paddingHorizontal: 6 },
  stat: { flex: 1, alignItems: 'center', gap: 2, paddingHorizontal: 4 },
  listCard: { paddingHorizontal: 14 },
  dayRow: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  dayRowMain: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 12, minHeight: 64 },
  dateTile: { width: 44, alignItems: 'center' },
  rowMeta: { marginTop: 4 },
  sheetBody: { gap: 14 },
  detailGrid: { flexDirection: 'row', padding: 14, gap: 8 },
  detail: { flex: 1, gap: 2 },
});
