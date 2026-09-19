// ============================================================
// ZAYRA MOBILE — Employee Dashboard Screen
// ============================================================

import React, { useEffect, useState, useCallback } from 'react';
import {
  View,
  Text,
  ScrollView,
  TouchableOpacity,
  RefreshControl,
  StyleSheet,
  Alert,
  Platform,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import * as Location from 'expo-location';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { dashboardApi, attendanceApi } from '@/api/services';
import { getDeviceInfo } from '@/utils/device';
import { formatTime, daysUntil } from '@/utils/date';
import { COLORS } from '@/config';
import { formatRiyadhBusinessDate } from '@/utils/businessDate';
import { navigateTo, type AppRoute } from '@/navigation/routes';
import type {
  EmployeeDashboard,
  TodayAttendance,
  PunchType,
  GeoLocation,
} from '@/types';

interface Props {
  navigation: any;
}

export default function EmployeeDashboardScreen({ navigation }: Props) {
  const { t } = useTranslation();
  const { user } = useAuthStore();

  const [dashboard, setDashboard] = useState<EmployeeDashboard | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [punchLoading, setPunchLoading] = useState(false);

  const loadDashboard = useCallback(async () => {
    try {
      const data = await dashboardApi.getEmployeeDashboard();
      setDashboard(data);
    } catch (err) {
      console.error('[Dashboard] Load error:', err);
    } finally {
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void loadDashboard();
  }, [loadDashboard]);

  const onRefresh = useCallback(() => {
    setRefreshing(true);
    void loadDashboard();
  }, [loadDashboard]);

  async function handlePunch(punchType: PunchType) {
    setPunchLoading(true);
    try {
      // Request location
      const { status } = await Location.requestForegroundPermissionsAsync();
      let geoLocation: GeoLocation | undefined;

      if (status === 'granted') {
        const loc = await Location.getCurrentPositionAsync({
          accuracy: Location.Accuracy.High,
        });
        geoLocation = {
          latitude: loc.coords.latitude,
          longitude: loc.coords.longitude,
          accuracy: loc.coords.accuracy ?? undefined,
          timestamp: loc.timestamp,
        };
      } else {
        // Check if policy requires location
        Alert.alert(
          'Location Required',
          t('attendance.locationRequired'),
          [{ text: 'OK' }]
        );
        return;
      }

      const deviceInfo = await getDeviceInfo();

      await attendanceApi.punch({
        punchType,
        timestamp: new Date().toISOString(),
        location: geoLocation,
        deviceInfo,
      });

      // Refresh dashboard after punch
      await loadDashboard();

      Alert.alert(
        'Success',
        punchType === 'CLOCK_IN' ? 'Clocked in successfully' : 'Clocked out successfully'
      );
    } catch (err: any) {
      Alert.alert(
        'Punch Failed',
        err?.response?.data?.message ?? 'Could not record attendance. Please try again.'
      );
    } finally {
      setPunchLoading(false);
    }
  }

  const attendance = dashboard?.todayAttendance;
  const canClockIn = !attendance?.currentlyActive;
  const canClockOut = !!attendance?.currentlyActive;

  const greeting = () => {
    const hour = new Date().getHours();
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    return 'Good evening';
  };

  const firstName = user?.fullName?.split(' ')[0] ?? 'there';
  const go = (route: AppRoute, params?: Record<string, unknown>) =>
    navigateTo(navigation, route, user, params);

  return (
    <ScrollView
      style={styles.container}
      contentContainerStyle={styles.content}
      refreshControl={
        <RefreshControl
          refreshing={refreshing}
          onRefresh={onRefresh}
          tintColor={COLORS.blue}
          colors={[COLORS.blue]}
        />
      }
      showsVerticalScrollIndicator={false}
    >
      {/* Header */}
      <LinearGradient
        colors={['#0B1020', '#0F1E40']}
        style={styles.header}
      >
        <View style={styles.headerTop}>
          <View>
            <Text style={styles.greeting}>{greeting()},</Text>
            <Text style={styles.name}>{firstName} 👋</Text>
          </View>
          <View style={styles.headerActions}>
            <TouchableOpacity
              style={styles.headerIcon}
              onPress={() => go('Notifications')}
            >
              <Ionicons name="notifications-outline" size={22} color="#fff" />
              {(dashboard?.unreadNotifications ?? 0) > 0 && (
                <View style={styles.badge}>
                  <Text style={styles.badgeText}>
                    {dashboard!.unreadNotifications > 9 ? '9+' : dashboard!.unreadNotifications}
                  </Text>
                </View>
              )}
            </TouchableOpacity>
            <TouchableOpacity
              style={styles.headerIcon}
              onPress={() => go('AIAssistant')}
            >
              <Ionicons name="sparkles-outline" size={22} color={COLORS.cyan} />
            </TouchableOpacity>
          </View>
        </View>

        {/* Date */}
        <Text style={styles.dateText}>
          {formatRiyadhBusinessDate()}
        </Text>
      </LinearGradient>

      {/* Today Attendance Card */}
      <View style={styles.section}>
        <AttendanceCard
          attendance={attendance}
          canClockIn={canClockIn}
          canClockOut={canClockOut}
          punchLoading={punchLoading}
          onClockIn={() => handlePunch('CLOCK_IN')}
          onClockOut={() => handlePunch('CLOCK_OUT')}
          onViewHistory={() => go('AttendanceHistory')}
        />
      </View>

      {/* Leave Balances */}
      {(dashboard?.leaveBalances?.length ?? 0) > 0 && (
        <View style={styles.section}>
          <SectionHeader
            title={t('dashboard.leaveBalance')}
            onSeeAll={() => go('ApplyLeave')}
          />
          <ScrollView horizontal showsHorizontalScrollIndicator={false}>
            {dashboard!.leaveBalances.slice(0, 4).map((lb) => (
              <View key={lb.leaveTypeId} style={styles.leaveCard}>
                <Text style={styles.leaveType} numberOfLines={1}>
                  {lb.leaveTypeName}
                </Text>
                <Text style={styles.leaveAvailable}>{lb.available}</Text>
                <Text style={styles.leaveLabel}>available</Text>
                <View style={styles.leaveBar}>
                  <View
                    style={[
                      styles.leaveBarFill,
                      {
                        width: `${Math.min(
                          100,
                          lb.allocated > 0 ? ((lb.available / lb.allocated) * 100) : 0
                        )}%` as any,
                      },
                    ]}
                  />
                </View>
                <Text style={styles.leaveUsed}>
                  {lb.used} used / {lb.allocated} total
                </Text>
              </View>
            ))}
          </ScrollView>
        </View>
      )}

      {/* Quick Actions */}
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Quick Actions</Text>
        <View style={styles.quickActions}>
          <QuickAction
            icon="calendar-outline"
            label="Apply Leave"
            color={COLORS.blue}
            onPress={() => go('ApplyLeave')}
          />
          <QuickAction
            icon="time-outline"
            label="Overtime"
            color="#7C3AED"
            onPress={() => go('Overtime')}
          />
          <QuickAction
            icon="document-text-outline"
            label="My Payslip"
            color={COLORS.success}
            onPress={() => go('Payslips')}
          />
          <QuickAction
            icon="help-circle-outline"
            label="HR Request"
            color={COLORS.warning}
            onPress={() => go('HRRequests')}
          />
        </View>
      </View>

      {/* Pending Requests */}
      {(dashboard?.pendingRequestsCount ?? 0) > 0 && (
        <View style={styles.section}>
          <TouchableOpacity
            style={styles.alertCard}
            onPress={() => go('HRRequests')}
          >
            <View style={styles.alertIcon}>
              <Ionicons name="time-outline" size={20} color={COLORS.warning} />
            </View>
            <View style={{ flex: 1 }}>
              <Text style={styles.alertTitle}>
                {dashboard!.pendingRequestsCount} Pending Request
                {dashboard!.pendingRequestsCount !== 1 ? 's' : ''}
              </Text>
              <Text style={styles.alertSubtitle}>Tap to view status</Text>
            </View>
            <Ionicons name="chevron-forward" size={16} color={COLORS.muted} />
          </TouchableOpacity>
        </View>
      )}

      {/* Expiring Documents */}
      {(dashboard?.expiringDocuments?.length ?? 0) > 0 && (
        <View style={styles.section}>
          <SectionHeader
            title="Document Alerts"
            onSeeAll={() => go('Documents')}
          />
          {dashboard!.expiringDocuments.map((doc) => {
            const days = daysUntil(doc.expiryDate!);
            return (
              <TouchableOpacity
                key={doc.id}
                style={styles.docAlert}
                onPress={() => go('Documents')}
              >
                <View
                  style={[
                    styles.docAlertDot,
                    { backgroundColor: days <= 0 ? COLORS.error : days <= 30 ? COLORS.warning : COLORS.success },
                  ]}
                />
                <View style={{ flex: 1 }}>
                  <Text style={styles.docAlertType}>{doc.documentType}</Text>
                  <Text style={styles.docAlertExpiry}>
                    {days <= 0
                      ? 'Expired'
                      : days === 1
                      ? 'Expires tomorrow'
                      : `Expires in ${days} days`}
                  </Text>
                </View>
                <Ionicons name="chevron-forward" size={14} color={COLORS.muted} />
              </TouchableOpacity>
            );
          })}
        </View>
      )}

      {/* Latest Payslip */}
      {dashboard?.latestPayslip && (
        <View style={styles.section}>
          <TouchableOpacity
            style={styles.payslipCard}
            onPress={() =>
              navigation.navigate('PayslipDetail', { id: dashboard.latestPayslip!.id })
            }
          >
            <LinearGradient
              colors={['#0F2040', '#0B1020']}
              style={styles.payslipGradient}
            >
              <View style={styles.payslipHeader}>
                <Text style={styles.payslipLabel}>Latest Payslip</Text>
                <View style={styles.payslipStatus}>
                  <View style={[styles.statusDot, { backgroundColor: COLORS.success }]} />
                  <Text style={styles.statusText}>{dashboard.latestPayslip.paymentStatus}</Text>
                </View>
              </View>
              <Text style={styles.payslipPeriod}>{dashboard.latestPayslip.periodLabel}</Text>
              <Text style={styles.payslipAmount}>
                {dashboard.latestPayslip.currency}{' '}
                {dashboard.latestPayslip.netSalary.toLocaleString('en-US', {
                  minimumFractionDigits: 2,
                })}
              </Text>
              <Text style={styles.payslipNet}>Net Salary</Text>
            </LinearGradient>
          </TouchableOpacity>
        </View>
      )}

      {/* Upcoming Holidays */}
      {(dashboard?.upcomingHolidays?.length ?? 0) > 0 && (
        <View style={styles.section}>
          <Text style={styles.sectionTitle}>Upcoming Holidays</Text>
          {dashboard!.upcomingHolidays.slice(0, 3).map((h) => (
            <View key={h.date} style={styles.holidayRow}>
              <View style={styles.holidayDate}>
                <Text style={styles.holidayDay}>
                  {new Date(h.date).toLocaleDateString('en-US', { day: '2-digit' })}
                </Text>
                <Text style={styles.holidayMonth}>
                  {new Date(h.date).toLocaleDateString('en-US', { month: 'short' })}
                </Text>
              </View>
              <View style={{ flex: 1 }}>
                <Text style={styles.holidayName}>{h.name}</Text>
                <Text style={styles.holidayType}>{h.type}</Text>
              </View>
            </View>
          ))}
        </View>
      )}

      <View style={{ height: 32 }} />
    </ScrollView>
  );
}

// ---- Sub Components ----

function AttendanceCard({
  attendance,
  canClockIn,
  canClockOut,
  punchLoading,
  onClockIn,
  onClockOut,
  onViewHistory,
}: {
  attendance?: TodayAttendance;
  canClockIn: boolean;
  canClockOut: boolean;
  punchLoading: boolean;
  onClockIn: () => void;
  onClockOut: () => void;
  onViewHistory: () => void;
}) {
  const statusColors: Record<string, string> = {
    PRESENT: COLORS.success,
    ABSENT: COLORS.error,
    LATE: COLORS.warning,
    HALF_DAY: COLORS.warning,
    ON_LEAVE: COLORS.blue,
    HOLIDAY: '#7C3AED',
    WEEKEND: COLORS.muted,
    MISSING_PUNCH: COLORS.error,
  };

  const statusColor = statusColors[attendance?.status ?? 'ABSENT'] ?? COLORS.muted;

  return (
    <View style={styles.attendanceCard}>
      <View style={styles.attendanceHeader}>
        <View>
          <Text style={styles.attendanceTitle}>Today's Attendance</Text>
          <View style={styles.statusRow}>
            <View style={[styles.statusBadge, { backgroundColor: `${statusColor}20` }]}>
              <View style={[styles.statusDot, { backgroundColor: statusColor }]} />
              <Text style={[styles.statusBadgeText, { color: statusColor }]}>
                {attendance?.status?.replace('_', ' ') ?? 'Not Recorded'}
              </Text>
            </View>
          </View>
        </View>
        <TouchableOpacity onPress={onViewHistory}>
          <Text style={styles.viewHistory}>History →</Text>
        </TouchableOpacity>
      </View>

      {/* Times */}
      <View style={styles.punchTimes}>
        <View style={styles.punchTime}>
          <Ionicons name="log-in-outline" size={16} color={COLORS.success} />
          <Text style={styles.punchLabel}>Clock In</Text>
          <Text style={styles.punchValue}>
            {attendance?.clockIn ? formatTime(attendance.clockIn) : '—'}
          </Text>
        </View>
        <View style={styles.punchDivider} />
        <View style={styles.punchTime}>
          <Ionicons name="log-out-outline" size={16} color={COLORS.error} />
          <Text style={styles.punchLabel}>Clock Out</Text>
          <Text style={styles.punchValue}>
            {attendance?.clockOut ? formatTime(attendance.clockOut) : '—'}
          </Text>
        </View>
        {attendance?.shiftStart && (
          <>
            <View style={styles.punchDivider} />
            <View style={styles.punchTime}>
              <Ionicons name="time-outline" size={16} color={COLORS.muted} />
              <Text style={styles.punchLabel}>Shift</Text>
              <Text style={styles.punchValue}>
                {formatTime(attendance.shiftStart)} – {formatTime(attendance.shiftEnd)}
              </Text>
            </View>
          </>
        )}
      </View>

      {/* Buttons */}
      <View style={styles.punchButtons}>
        {canClockIn && (
          <TouchableOpacity
            style={[styles.punchBtn, styles.clockInBtn]}
            onPress={onClockIn}
            disabled={punchLoading}
          >
            <Ionicons name="log-in-outline" size={18} color="#fff" />
            <Text style={styles.punchBtnText}>Clock In</Text>
          </TouchableOpacity>
        )}
        {canClockOut && (
          <TouchableOpacity
            style={[styles.punchBtn, styles.clockOutBtn]}
            onPress={onClockOut}
            disabled={punchLoading}
          >
            <Ionicons name="log-out-outline" size={18} color="#fff" />
            <Text style={styles.punchBtnText}>Clock Out</Text>
          </TouchableOpacity>
        )}
      </View>
    </View>
  );
}

function SectionHeader({ title, onSeeAll }: { title: string; onSeeAll: () => void }) {
  return (
    <View style={styles.sectionHeader}>
      <Text style={styles.sectionTitle}>{title}</Text>
      <TouchableOpacity onPress={onSeeAll}>
        <Text style={styles.seeAll}>See All</Text>
      </TouchableOpacity>
    </View>
  );
}

function QuickAction({
  icon,
  label,
  color,
  onPress,
}: {
  icon: string;
  label: string;
  color: string;
  onPress: () => void;
}) {
  return (
    <TouchableOpacity style={styles.quickAction} onPress={onPress} activeOpacity={0.7}>
      <View style={[styles.quickActionIcon, { backgroundColor: `${color}15` }]}>
        <Ionicons name={icon as any} size={22} color={color} />
      </View>
      <Text style={styles.quickActionLabel}>{label}</Text>
    </TouchableOpacity>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.bg },
  content: { paddingBottom: 40 },
  header: {
    paddingTop: Platform.OS === 'ios' ? 56 : 40,
    paddingHorizontal: 20,
    paddingBottom: 24,
  },
  headerTop: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    marginBottom: 8,
  },
  headerActions: { flexDirection: 'row', gap: 8 },
  headerIcon: {
    width: 40,
    height: 40,
    borderRadius: 12,
    backgroundColor: 'rgba(255,255,255,0.1)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  badge: {
    position: 'absolute',
    top: -4,
    right: -4,
    backgroundColor: COLORS.error,
    borderRadius: 8,
    minWidth: 16,
    height: 16,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 3,
  },
  badgeText: { fontSize: 10, fontWeight: '700', color: '#fff' },
  greeting: { fontSize: 14, color: 'rgba(255,255,255,0.6)' },
  name: { fontSize: 22, fontWeight: '700', color: '#fff' },
  dateText: { fontSize: 13, color: 'rgba(255,255,255,0.5)' },
  section: { paddingHorizontal: 16, marginTop: 16 },
  sectionHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: 10,
  },
  sectionTitle: { fontSize: 16, fontWeight: '700', color: COLORS.text, marginBottom: 10 },
  seeAll: { fontSize: 13, color: COLORS.blue, fontWeight: '600' },
  // Attendance card
  attendanceCard: {
    backgroundColor: COLORS.card,
    borderRadius: 16,
    padding: 16,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.06,
    shadowRadius: 8,
    elevation: 3,
  },
  attendanceHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    marginBottom: 16,
  },
  attendanceTitle: { fontSize: 16, fontWeight: '700', color: COLORS.text, marginBottom: 6 },
  statusRow: { flexDirection: 'row' },
  statusBadge: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 20,
    gap: 5,
  },
  statusDot: { width: 7, height: 7, borderRadius: 4 },
  statusBadgeText: { fontSize: 13, fontWeight: '600' },
  viewHistory: { fontSize: 13, color: COLORS.blue, fontWeight: '600' },
  punchTimes: {
    flexDirection: 'row',
    backgroundColor: COLORS.bg,
    borderRadius: 12,
    padding: 12,
    marginBottom: 14,
  },
  punchTime: { flex: 1, alignItems: 'center', gap: 3 },
  punchDivider: { width: 1, backgroundColor: COLORS.border, marginHorizontal: 8 },
  punchLabel: { fontSize: 11, color: COLORS.muted, marginTop: 2 },
  punchValue: { fontSize: 15, fontWeight: '700', color: COLORS.text },
  punchButtons: { flexDirection: 'row', gap: 10 },
  punchBtn: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 12,
    borderRadius: 12,
    gap: 6,
  },
  clockInBtn: { backgroundColor: COLORS.success },
  clockOutBtn: { backgroundColor: '#EF4444' },
  punchBtnText: { fontSize: 15, fontWeight: '700', color: '#fff' },
  // Leave cards
  leaveCard: {
    backgroundColor: COLORS.card,
    borderRadius: 14,
    padding: 14,
    width: 130,
    marginRight: 10,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.05,
    shadowRadius: 4,
    elevation: 2,
  },
  leaveType: { fontSize: 12, color: COLORS.muted, marginBottom: 4 },
  leaveAvailable: { fontSize: 28, fontWeight: '800', color: COLORS.text },
  leaveLabel: { fontSize: 11, color: COLORS.muted, marginBottom: 8 },
  leaveBar: {
    height: 4,
    backgroundColor: COLORS.border,
    borderRadius: 2,
    marginBottom: 6,
    overflow: 'hidden',
  },
  leaveBarFill: { height: '100%', backgroundColor: COLORS.blue, borderRadius: 2 },
  leaveUsed: { fontSize: 10, color: COLORS.muted },
  // Quick actions
  quickActions: { flexDirection: 'row', justifyContent: 'space-between' },
  quickAction: { alignItems: 'center', flex: 1 },
  quickActionIcon: {
    width: 52,
    height: 52,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 6,
  },
  quickActionLabel: { fontSize: 11, color: COLORS.text, fontWeight: '500', textAlign: 'center' },
  // Alert card
  alertCard: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: `${COLORS.warning}15`,
    borderRadius: 12,
    padding: 14,
    gap: 12,
    borderWidth: 1,
    borderColor: `${COLORS.warning}30`,
  },
  alertIcon: {
    width: 36,
    height: 36,
    borderRadius: 10,
    backgroundColor: `${COLORS.warning}20`,
    alignItems: 'center',
    justifyContent: 'center',
  },
  alertTitle: { fontSize: 14, fontWeight: '600', color: COLORS.text },
  alertSubtitle: { fontSize: 12, color: COLORS.muted, marginTop: 2 },
  // Doc alerts
  docAlert: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: COLORS.card,
    borderRadius: 12,
    padding: 12,
    marginBottom: 8,
    gap: 12,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.04,
    shadowRadius: 3,
    elevation: 1,
  },
  docAlertDot: { width: 10, height: 10, borderRadius: 5 },
  docAlertType: { fontSize: 14, fontWeight: '600', color: COLORS.text },
  docAlertExpiry: { fontSize: 12, color: COLORS.muted, marginTop: 2 },
  // Payslip
  payslipCard: { borderRadius: 16, overflow: 'hidden' },
  payslipGradient: { padding: 20 },
  payslipHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: 4,
  },
  payslipLabel: { fontSize: 12, color: 'rgba(255,255,255,0.5)', fontWeight: '600' },
  payslipStatus: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 5,
    backgroundColor: 'rgba(0,200,150,0.15)',
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 20,
  },
  statusText: { fontSize: 11, color: COLORS.success, fontWeight: '600' },
  payslipPeriod: { fontSize: 14, color: 'rgba(255,255,255,0.7)', marginBottom: 8 },
  payslipAmount: { fontSize: 30, fontWeight: '800', color: '#fff', letterSpacing: -0.5 },
  payslipNet: { fontSize: 12, color: 'rgba(255,255,255,0.5)', marginTop: 2 },
  // Holidays
  holidayRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 14,
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: COLORS.border,
  },
  holidayDate: {
    width: 44,
    height: 44,
    borderRadius: 12,
    backgroundColor: `${COLORS.blue}15`,
    alignItems: 'center',
    justifyContent: 'center',
  },
  holidayDay: { fontSize: 16, fontWeight: '800', color: COLORS.blue },
  holidayMonth: { fontSize: 10, color: COLORS.blue, fontWeight: '600' },
  holidayName: { fontSize: 14, fontWeight: '600', color: COLORS.text },
  holidayType: { fontSize: 11, color: COLORS.muted, marginTop: 2 },
});
