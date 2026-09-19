// ============================================================
// ZAYRA MOBILE — Attendance History Screen
// ============================================================

import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, StyleSheet,
  ActivityIndicator, Platform,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { attendanceApi } from '@/api/services';
import { formatTime, formatWorkHours } from '@/utils/date';
import { parseISO } from 'date-fns';
import { COLORS } from '@/config';
import { navigateTo, isManagerUser } from '@/navigation/routes';
import { useAuthStore } from '@/auth/authStore';
import type { AttendanceDay } from '@/types';

interface Props {
  navigation: any;
}

export default function AttendanceHistoryScreen({ navigation }: Props) {
  const { user } = useAuthStore();

  const now = new Date();
  const [year, setYear] = useState(now.getFullYear());
  const [month, setMonth] = useState(now.getMonth() + 1);
  const [records, setRecords] = useState<AttendanceDay[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await attendanceApi.getMonthlyAttendance(year, month);
      setRecords(data);
    } catch (err: any) {
      // Fail loudly: an empty list here reads as "no attendance", which is wrong.
      console.error('[AttendanceHistory]', err);
      setRecords([]);
      setError(err?.message ?? 'Could not load attendance.');
    } finally {
      setLoading(false);
    }
  }, [month, year]);

  useEffect(() => {
    void load();
  }, [load]);

  function prevMonth() {
    if (month === 1) { setYear(y => y - 1); setMonth(12); }
    else setMonth(m => m - 1);
  }

  function nextMonth() {
    const nowMonth = new Date().getMonth() + 1;
    const nowYear = new Date().getFullYear();
    if (year === nowYear && month === nowMonth) return;
    if (month === 12) { setYear(y => y + 1); setMonth(1); }
    else setMonth(m => m + 1);
  }

  const monthLabel = new Date(year, month - 1, 1).toLocaleDateString('en-US', {
    month: 'long', year: 'numeric',
  });

  // Summary stats
  const summary = records.reduce(
    (acc, r) => {
      if (r.status === 'PRESENT') acc.present++;
      else if (r.status === 'ABSENT') acc.absent++;
      else if (r.status === 'LATE') { acc.present++; acc.late++; }
      else if (r.status === 'ON_LEAVE') acc.onLeave++;
      acc.totalHours += r.workHours ?? 0;
      return acc;
    },
    { present: 0, absent: 0, late: 0, onLeave: 0, totalHours: 0 }
  );

  const STATUS_CONFIG: Record<string, { color: string; bg: string; label: string }> = {
    PRESENT: { color: COLORS.success, bg: `${COLORS.success}15`, label: 'Present' },
    ABSENT: { color: COLORS.error, bg: `${COLORS.error}15`, label: 'Absent' },
    LATE: { color: COLORS.warning, bg: `${COLORS.warning}15`, label: 'Late' },
    HALF_DAY: { color: COLORS.warning, bg: `${COLORS.warning}15`, label: 'Half Day' },
    ON_LEAVE: { color: COLORS.blue, bg: `${COLORS.blue}15`, label: 'On Leave' },
    HOLIDAY: { color: '#7C3AED', bg: '#7C3AED15', label: 'Holiday' },
    WEEKEND: { color: COLORS.muted, bg: `${COLORS.muted}15`, label: 'Weekend' },
    MISSING_PUNCH: { color: COLORS.error, bg: `${COLORS.error}15`, label: 'Missing Punch' },
  };

  return (
    <View style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity onPress={() => navigation.goBack()}>
          <Ionicons name="arrow-back" size={24} color={COLORS.text} />
        </TouchableOpacity>
        <Text style={styles.headerTitle}>Attendance History</Text>
        <TouchableOpacity onPress={() => navigateTo(navigation, 'AttendanceCorrection', isManagerUser(user))}>
          <Ionicons name="create-outline" size={22} color={COLORS.blue} />
        </TouchableOpacity>
      </View>

      {/* Month picker */}
      <View style={styles.monthPicker}>
        <TouchableOpacity onPress={prevMonth} style={styles.monthArrow}>
          <Ionicons name="chevron-back" size={20} color={COLORS.text} />
        </TouchableOpacity>
        <Text style={styles.monthLabel}>{monthLabel}</Text>
        <TouchableOpacity onPress={nextMonth} style={styles.monthArrow}>
          <Ionicons name="chevron-forward" size={20} color={COLORS.text} />
        </TouchableOpacity>
      </View>

      {/* Summary row */}
      <View style={styles.summaryRow}>
        <SummaryChip label="Present" value={summary.present} color={COLORS.success} />
        <SummaryChip label="Absent" value={summary.absent} color={COLORS.error} />
        <SummaryChip label="Late" value={summary.late} color={COLORS.warning} />
        <SummaryChip label="Leave" value={summary.onLeave} color={COLORS.blue} />
        <SummaryChip
          label="Hours"
          value={formatWorkHours(summary.totalHours)}
          color={COLORS.text}
          isString
        />
      </View>

      {/* List */}
      {loading ? (
        <View style={styles.centered}>
          <ActivityIndicator color={COLORS.blue} size="large" />
        </View>
      ) : (
        <ScrollView
          style={styles.list}
          contentContainerStyle={styles.listContent}
          showsVerticalScrollIndicator={false}
        >
          {error ? (
            <View style={styles.centered}>
              <Ionicons name="alert-circle-outline" size={48} color={COLORS.error} />
              <Text style={styles.emptyText}>{error}</Text>
              <TouchableOpacity onPress={load} style={{ marginTop: 12 }}>
                <Text style={{ color: COLORS.blue, fontWeight: '600' }}>Try again</Text>
              </TouchableOpacity>
            </View>
          ) : records.length === 0 ? (
            <View style={styles.centered}>
              <Ionicons name="calendar-outline" size={48} color={COLORS.muted} />
              <Text style={styles.emptyText}>No attendance recorded this month</Text>
            </View>
          ) : (
            records.map((record) => {
              const cfg = STATUS_CONFIG[record.status] ?? STATUS_CONFIG.ABSENT;
              return (
                <View key={record.date} style={styles.recordCard}>
                  {/* Date column */}
                  <View style={styles.dateCol}>
                    <Text style={styles.recordDay}>
                      {parseISO(record.date).toLocaleDateString('en-US', { weekday: 'short' })}
                    </Text>
                    <Text style={styles.recordDate}>
                      {parseISO(record.date).getDate()}
                    </Text>
                  </View>

                  {/* Status & times */}
                  <View style={styles.recordMain}>
                    <View style={styles.recordRow}>
                      <View style={[styles.statusChip, { backgroundColor: cfg.bg }]}>
                        <Text style={[styles.statusChipText, { color: cfg.color }]}>
                          {cfg.label}
                        </Text>
                      </View>
                      {record.shiftName && (
                        <Text style={styles.shiftName}>{record.shiftName}</Text>
                      )}
                      {/* `0 && …` would render a bare "0" outside <Text> and crash the list */}
                      {(record.lateMinutes ?? 0) > 0 && (
                        <View style={styles.lateBadge}>
                          <Ionicons name="warning-outline" size={11} color={COLORS.warning} />
                          <Text style={styles.lateBadgeText}>
                            {record.lateMinutes}m late
                          </Text>
                        </View>
                      )}
                    </View>
                    <View style={styles.timeRow}>
                      <TimeEntry icon="log-in-outline" time={record.clockIn} />
                      {record.clockIn && record.clockOut && (
                        <Ionicons name="remove-outline" size={14} color={COLORS.muted} />
                      )}
                      <TimeEntry icon="log-out-outline" time={record.clockOut} />
                      {(record.workHours ?? 0) > 0 && (
                        <>
                          <View style={styles.timeDot} />
                          <Text style={styles.workHours}>
                            {formatWorkHours(record.workHours!)}
                          </Text>
                        </>
                      )}
                    </View>
                  </View>

                  {/* Actions */}
                  <View style={styles.recordActions}>
                    {record.status === 'MISSING_PUNCH' && (
                      <TouchableOpacity
                        style={styles.correctionBtn}
                        onPress={() =>
                          navigateTo(navigation, 'AttendanceCorrection', isManagerUser(user), { date: record.date })
                        }
                      >
                        <Ionicons name="create-outline" size={14} color={COLORS.blue} />
                      </TouchableOpacity>
                    )}
                  </View>
                </View>
              );
            })
          )}
          <View style={{ height: 40 }} />
        </ScrollView>
      )}
    </View>
  );
}

function SummaryChip({
  label, value, color, isString,
}: { label: string; value: number | string; color: string; isString?: boolean }) {
  return (
    <View style={styles.summaryChip}>
      <Text style={[styles.summaryValue, { color }]}>
        {isString ? value : value}
      </Text>
      <Text style={styles.summaryLabel}>{label}</Text>
    </View>
  );
}

function TimeEntry({ icon, time }: { icon: string; time?: string }) {
  return (
    <View style={styles.timeEntry}>
      <Ionicons name={icon as any} size={12} color={COLORS.muted} />
      <Text style={styles.timeText}>{time ? formatTime(time) : '—'}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.bg },
  header: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    paddingHorizontal: 16, paddingTop: Platform.OS === 'ios' ? 56 : 40, paddingBottom: 16,
    backgroundColor: COLORS.card,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.05, shadowRadius: 3, elevation: 2,
  },
  headerTitle: { fontSize: 17, fontWeight: '700', color: COLORS.text },
  monthPicker: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    paddingHorizontal: 20, paddingVertical: 14,
    backgroundColor: COLORS.card, borderBottomWidth: 1, borderBottomColor: COLORS.border,
  },
  monthArrow: { padding: 4 },
  monthLabel: { fontSize: 16, fontWeight: '700', color: COLORS.text },
  summaryRow: {
    flexDirection: 'row', backgroundColor: COLORS.card,
    paddingHorizontal: 16, paddingVertical: 12,
    borderBottomWidth: 1, borderBottomColor: COLORS.border,
    gap: 4,
  },
  summaryChip: { flex: 1, alignItems: 'center' },
  summaryValue: { fontSize: 16, fontWeight: '800' },
  summaryLabel: { fontSize: 10, color: COLORS.muted, marginTop: 2 },
  list: { flex: 1 },
  listContent: { padding: 16 },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', paddingTop: 60 },
  emptyText: { fontSize: 14, color: COLORS.muted, marginTop: 12 },
  recordCard: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: COLORS.card, borderRadius: 12, padding: 14, marginBottom: 8, gap: 14,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.04, shadowRadius: 3, elevation: 1,
  },
  dateCol: { width: 40, alignItems: 'center' },
  recordDay: { fontSize: 11, color: COLORS.muted, fontWeight: '600', marginBottom: 2 },
  recordDate: { fontSize: 20, fontWeight: '800', color: COLORS.text },
  recordMain: { flex: 1 },
  recordRow: { flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 6, flexWrap: 'wrap' },
  statusChip: { paddingHorizontal: 8, paddingVertical: 3, borderRadius: 20 },
  statusChipText: { fontSize: 12, fontWeight: '600' },
  shiftName: { fontSize: 11, color: COLORS.muted },
  lateBadge: {
    flexDirection: 'row', alignItems: 'center', gap: 3,
    backgroundColor: `${COLORS.warning}15`, paddingHorizontal: 6, paddingVertical: 2, borderRadius: 20,
  },
  lateBadgeText: { fontSize: 10, color: COLORS.warning, fontWeight: '600' },
  timeRow: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  timeEntry: { flexDirection: 'row', alignItems: 'center', gap: 3 },
  timeText: { fontSize: 12, color: COLORS.textSecondary, fontWeight: '500' },
  timeDot: { width: 3, height: 3, borderRadius: 1.5, backgroundColor: COLORS.muted },
  workHours: { fontSize: 12, color: COLORS.blue, fontWeight: '600' },
  recordActions: {},
  correctionBtn: {
    width: 30, height: 30, borderRadius: 8,
    backgroundColor: `${COLORS.blue}15`,
    alignItems: 'center', justifyContent: 'center',
  },
});
