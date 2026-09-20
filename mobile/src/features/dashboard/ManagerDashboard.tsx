import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Location from 'expo-location';
import { useAuthStore } from '@/auth/authStore';
import { attendanceApi, dashboardApi } from '@/api/services';
import { AttendanceSelfieModal } from '@/features/attendance/AttendanceSelfieModal';
import { AttendanceCard } from '@/features/dashboard/EmployeeDashboard';
import { getDeviceInfo } from '@/utils/device';
import { navigateTo, isManagerUser, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import {
  EmployeeAvatar,
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SwipeDeck,
} from '@/components/ui';
import type { GeoLocation, ManagerDashboard, PunchType, TodayAttendance } from '@/types';

interface Props {
  navigation: any;
}

export default function ManagerDashboardScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const [dashboard, setDashboard] = useState<ManagerDashboard | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [attendance, setAttendance] = useState<TodayAttendance | undefined>();
  const [attendanceLoading, setAttendanceLoading] = useState(false);
  const [punchLoading, setPunchLoading] = useState(false);
  const [pendingPunchType, setPendingPunchType] = useState<PunchType | null>(null);
  const [punchFeedback, setPunchFeedback] = useState<PunchType | null>(null);
  const employeeId = Number(user?.employeeId);
  const hasEmployeeProfile = Number.isFinite(employeeId) && employeeId > 0;

  const loadPersonalAttendance = useCallback(async () => {
    if (!hasEmployeeProfile) {
      setAttendance(undefined);
      return;
    }
    setAttendanceLoading(true);
    try {
      setAttendance(await attendanceApi.getTodayAttendance());
    } catch (error) {
      console.error('[ManagerDashboard] Personal attendance:', error);
    } finally {
      setAttendanceLoading(false);
    }
  }, [hasEmployeeProfile]);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [managerDashboard] = await Promise.all([
        dashboardApi.getManagerDashboard(),
        loadPersonalAttendance(),
      ]);
      setDashboard(managerDashboard);
    } catch (error) {
      console.error('[ManagerDashboard]', error);
      setLoadError('We couldn’t load your manager workspace. Check your connection and try again.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [loadPersonalAttendance]);
  useEffect(() => {
    void load();
  }, [load]);

  const onRefresh = useCallback(() => {
    setRefreshing(true);
    void load();
  }, [load]);

  const firstName = user?.fullName?.split(' ')[0] ?? 'Manager';
  const go = (route: AppRoute, params?: Record<string, unknown>) =>
    navigateTo(navigation, route, isManagerUser(user), params);
  const team = dashboard?.teamSummary;
  const pendingCount = dashboard?.pendingApprovalsCount ?? 0;

  const submitPunch = useCallback(async (
    punchType: PunchType,
    selfiePhotoReference: string,
    verification?: { deviceFaceVerified: boolean; faceCapabilityAvailable: boolean },
  ) => {
    setPunchLoading(true);
    try {
      const { status } = await Location.requestForegroundPermissionsAsync();
      if (status !== 'granted') {
        Alert.alert('Location required', 'Allow location access to verify where this attendance event occurred.');
        return;
      }
      const location = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.High });
      const geoLocation: GeoLocation = {
        latitude: location.coords.latitude,
        longitude: location.coords.longitude,
        accuracy: location.coords.accuracy ?? undefined,
        timestamp: location.timestamp,
        mocked: (location as any).mocked ?? false,
      };
      await attendanceApi.punch({
        punchType,
        timestamp: new Date().toISOString(),
        location: geoLocation,
        deviceInfo: await getDeviceInfo(),
        selfiePhotoReference,
        deviceFaceVerified: verification?.deviceFaceVerified,
        faceCapabilityAvailable: verification?.faceCapabilityAvailable,
      });
      await loadPersonalAttendance();
      setPunchFeedback(punchType);
    } catch (error: any) {
      Alert.alert(
        'Could not record attendance',
        error?.response?.data?.message ?? error?.message ?? 'Please check your connection and try again.',
      );
      throw error;
    } finally {
      setPunchLoading(false);
    }
  }, [loadPersonalAttendance]);

  const handleSelfieConfirm = useCallback(async (
    uri: string,
    verification: { deviceFaceVerified: boolean; faceCapabilityAvailable: boolean },
  ) => {
    if (!pendingPunchType) return;
    try {
      const evidence = await attendanceApi.uploadSelfie(uri);
      await submitPunch(pendingPunchType, evidence.photoReference, verification);
      setPendingPunchType(null);
    } catch {
      // The modal stays open so the user can retry without losing the captured image.
    }
  }, [pendingPunchType, submitPunch]);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={onRefresh}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Manager workspace"
          title={`Lead with clarity, ${firstName}`}
          subtitle={new Date().toLocaleDateString('en-US', {
            weekday: 'long',
            month: 'long',
            day: 'numeric',
          })}
          actions={
            <View style={styles.heroActions}>
              <MotionPressable
                onPress={() => go('Profile')}
                haptic="selection"
                accessibilityRole="button"
                accessibilityLabel="Open employee profile"
                contentStyle={styles.profileAvatarButton}
              >
                <EmployeeAvatar
                  name={user?.fullName ?? 'Manager'}
                  photoUrl={user?.profilePhotoUrl}
                  size={44}
                  ring
                />
              </MotionPressable>
              <GlassIconButton
                icon="notifications-outline"
                label="Notifications"
                onPress={() => go('Notifications')}
              />
            </View>
          }
        />
        <View style={styles.section}>
          {hasEmployeeProfile ? (
            attendanceLoading && !attendance ? (
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.attendanceLoadingCard}>
                <ActivityIndicator color={theme.colors.primary} />
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading your attendance…</Text>
              </GlassSurface>
            ) : (
              <AttendanceCard
                attendance={attendance}
                canClockIn={!attendance?.currentlyActive}
                canClockOut={!!attendance?.currentlyActive}
                punchLoading={punchLoading}
                onClockIn={() => setPendingPunchType('CLOCK_IN')}
                onClockOut={() => setPendingPunchType('CLOCK_OUT')}
                onViewHistory={() => go('AttendanceHistory')}
                feedback={punchFeedback}
              />
            )
          ) : (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.employeeLinkCard}>
              <View style={[styles.employeeLinkIcon, { backgroundColor: `${theme.colors.warning}1E` }]}>
                <Ionicons name="person-add-outline" size={24} color={theme.colors.warning} />
              </View>
              <View style={styles.employeeLinkCopy}>
                <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Personal attendance needs an employee profile</Text>
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 5 }]}>
                  This administrator login is not linked to an employee record. Ask HR to link it before using selfie attendance.
                </Text>
              </View>
            </GlassSurface>
          )}
        </View>
        {loading ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading manager workspace…</Text>
            </GlassSurface>
          </View>
        ) : loadError ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <Ionicons name="cloud-offline-outline" size={28} color={theme.colors.danger} />
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Workspace unavailable</Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, textAlign: 'center' }]}>{loadError}</Text>
              <MotionPressable onPress={() => void load()} contentStyle={[styles.retryButton, { backgroundColor: theme.colors.primary }]}>
                <Text style={[theme.typography.bodyStrong, { color: '#FFFFFF' }]}>Try again</Text>
              </MotionPressable>
            </GlassSurface>
          </View>
        ) : null}
        {!loading && !loadError ? (
          <View style={styles.deckSection}>
            <SectionHeader
              title="Manager command center"
              subtitle="Swipe between team, approvals and actions"
            />
            <SwipeDeck minHeight={344}>
              <GlassSurface
                elevated={false}
                radius={theme.radius.xl}
                contentStyle={styles.deckPage}
              >
                <View style={styles.deckHeading}>
                  <View style={[styles.deckIcon, { backgroundColor: theme.colors.primary + '18' }]}>
                    <Ionicons name="people-outline" size={21} color={theme.colors.primary} />
                  </View>
                  <View style={styles.deckHeadingCopy}>
                    <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Team today</Text>
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                      Live workforce snapshot
                    </Text>
                  </View>
                  <MotionPressable
                    accessibilityRole="button"
                    accessibilityLabel="Open team"
                    onPress={() => go('Team')}
                    haptic="selection"
                    contentStyle={styles.deckLink}
                  >
                    <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
                      Open
                    </Text>
                  </MotionPressable>
                </View>

                {team ? (
                  <>
                    <View style={styles.metricGrid}>
                      <TeamMetric label="Total" value={team.total} icon="people-outline" accent={theme.colors.primary} />
                      <TeamMetric label="Present" value={team.present} icon="checkmark-circle-outline" accent={theme.colors.success} />
                      <TeamMetric label="Absent" value={team.absent} icon="close-circle-outline" accent={theme.colors.danger} />
                      <TeamMetric label="On leave" value={team.onLeave} icon="airplane-outline" accent={theme.colors.violet} />
                    </View>
                    {team.lateToday > 0 ? (
                      <MotionPressable
                        onPress={() => go('Team')}
                        haptic="selection"
                        contentStyle={styles.rounded}
                        style={styles.lateShell}
                      >
                        <View style={[styles.deckAlert, { backgroundColor: theme.colors.surfaceSoft }]}>
                          <Ionicons name="warning-outline" size={20} color={theme.colors.warning} />
                          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>
                            {team.lateToday} late today
                          </Text>
                          <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
                        </View>
                      </MotionPressable>
                    ) : null}
                  </>
                ) : (
                  <View style={styles.deckEmpty}>
                    <Ionicons name="people-outline" size={26} color={theme.colors.textMuted} />
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                      Team data is not available yet.
                    </Text>
                  </View>
                )}
              </GlassSurface>

              <GlassSurface
                elevated={false}
                radius={theme.radius.xl}
                contentStyle={styles.deckPage}
              >
                <View style={styles.deckHeading}>
                  <View style={[styles.deckIcon, { backgroundColor: theme.colors.warning + '18' }]}>
                    <Ionicons name="checkmark-done-outline" size={21} color={theme.colors.warning} />
                  </View>
                  <View style={styles.deckHeadingCopy}>
                    <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Approvals</Text>
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                      Decisions that need your attention
                    </Text>
                  </View>
                  <View style={[styles.deckCount, { backgroundColor: theme.colors.warning + '18' }]}>
                    <Text style={[theme.typography.bodyStrong, { color: theme.colors.warning }]}>{pendingCount}</Text>
                  </View>
                </View>

                {pendingCount > 0 ? (
                  <MotionPressable
                    onPress={() => go('Approvals')}
                    haptic="medium"
                    contentStyle={styles.rounded}
                  >
                    <View style={[styles.deckApprovalHero, { backgroundColor: theme.colors.surfaceSoft }]}>
                      <View style={[styles.approvalIcon, { backgroundColor: theme.colors.primary + '20' }]}>
                        <Ionicons name="checkmark-done-outline" size={24} color={theme.colors.primary} />
                      </View>
                      <View style={styles.approvalCopy}>
                        <Text style={[theme.typography.h3, { color: theme.colors.text }]}>
                          {pendingCount} waiting
                        </Text>
                        <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                          Tap to review the queue
                        </Text>
                      </View>
                      <Ionicons name="arrow-forward" size={20} color={theme.colors.primary} />
                    </View>
                  </MotionPressable>
                ) : (
                  <View style={styles.deckClear}>
                    <Ionicons name="checkmark-circle-outline" size={28} color={theme.colors.success} />
                    <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>All caught up</Text>
                  </View>
                )}

                {dashboard?.pendingByType && Object.values(dashboard.pendingByType).some((count) => count > 0) ? (
                  <View style={[styles.deckApprovalList, { borderTopColor: theme.colors.divider }]}>
                    {Object.entries(dashboard.pendingByType)
                      .filter(([, count]) => count > 0)
                      .slice(0, 3)
                      .map(([type, count], index, items) => (
                        <ApprovalTypeRow
                          key={type}
                          type={type}
                          count={count}
                          isLast={index === items.length - 1}
                          onPress={() => go('Approvals', { filterType: type })}
                        />
                      ))}
                  </View>
                ) : null}
              </GlassSurface>

              <GlassSurface
                elevated={false}
                radius={theme.radius.xl}
                contentStyle={styles.deckPage}
              >
                <View style={styles.deckHeading}>
                  <View style={[styles.deckIcon, { backgroundColor: theme.colors.cyan + '18' }]}>
                    <Ionicons name="grid-outline" size={21} color={theme.colors.cyan} />
                  </View>
                  <View style={styles.deckHeadingCopy}>
                    <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Manager actions</Text>
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                      Move work forward quickly
                    </Text>
                  </View>
                </View>

                <View style={styles.quickGrid}>
                  <ManagerAction
                    icon="people-outline"
                    title="My team"
                    subtitle="Attendance & people"
                    accent={theme.colors.primary}
                    onPress={() => go('Team')}
                  />
                  <ManagerAction
                    icon="calendar-outline"
                    title="My leave"
                    subtitle="Personal requests"
                    accent={theme.colors.success}
                    onPress={() => go('ApplyLeave')}
                  />
                  <ManagerAction
                    icon="checkmark-done-outline"
                    title="Approvals"
                    subtitle="Review requests"
                    accent={theme.colors.warning}
                    onPress={() => go('Approvals')}
                  />
                  <ManagerAction
                    icon="sparkles-outline"
                    title="AI insights"
                    subtitle="Workforce signals"
                    accent={theme.colors.cyan}
                    onPress={() => go('AIAssistant')}
                  />
                </View>

                {dashboard?.overtimeAlert && dashboard.overtimeAlert.thisMonth > 0 ? (
                  <MotionPressable
                    onPress={() => go('Approvals', { filterType: 'OVERTIME' })}
                    haptic="selection"
                    contentStyle={styles.rounded}
                    style={styles.deckOvertimeShell}
                  >
                    <View style={[styles.deckOvertime, { backgroundColor: theme.colors.surfaceSoft }]}>
                      <Ionicons name="time-outline" size={20} color={theme.colors.warning} />
                      <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>
                        {dashboard.overtimeAlert.thisMonth}h overtime this month
                      </Text>
                      <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
                    </View>
                  </MotionPressable>
                ) : null}
              </GlassSurface>
            </SwipeDeck>
          </View>
        ) : null}

        <View style={styles.bottomSpacer} />
      </ScrollView>
      <AttendanceSelfieModal
        visible={pendingPunchType !== null}
        punchType={pendingPunchType}
        onCancel={() => setPendingPunchType(null)}
        onConfirm={handleSelfieConfirm}
      />
    </View>
  );
}

function TeamMetric({
  label,
  value,
  icon,
  accent,
}: {
  label: string;
  value: number;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  accent: string;
}) {
  const { theme } = useTheme();
  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.metricCard} contentStyle={styles.metricContent}>
      <View style={[styles.metricIcon, { backgroundColor: `${accent}19` }]}>
        <Ionicons name={icon} size={21} color={accent} />
      </View>
      <Text style={[styles.metricValue, { color: theme.colors.text }]}>{value}</Text>
      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>{label}</Text>
    </GlassSurface>
  );
}
function ApprovalTypeRow({
  type,
  count,
  isLast,
  onPress,
}: {
  type: string;
  count: number;
  isLast: boolean;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const map: Record<string, { label: string; icon: any; accent: string }> = {
    LEAVE: { label: 'Leave requests', icon: 'airplane-outline', accent: theme.colors.primary },
    OVERTIME: { label: 'Overtime requests', icon: 'time-outline', accent: theme.colors.violet },
    ATTENDANCE_CORRECTION: { label: 'Attendance corrections', icon: 'create-outline', accent: theme.colors.warning },
    RECRUITMENT_REQUISITION: { label: 'Recruitment', icon: 'briefcase-outline', accent: theme.colors.success },
    HR_REQUEST: { label: 'HR requests', icon: 'help-circle-outline', accent: theme.colors.cyan },
  };
  const meta = map[type] ?? {
    label: type.replaceAll('_', ' ').toLowerCase(),
    icon: 'document-outline',
    accent: theme.colors.textMuted,
  };

  return (
    <MotionPressable onPress={onPress} haptic="selection" contentStyle={styles.rounded}>
      <View
        style={[
          styles.approvalRow,
          !isLast && { borderBottomColor: theme.colors.divider, borderBottomWidth: StyleSheet.hairlineWidth },
        ]}
      >
        <View style={[styles.approvalRowIcon, { backgroundColor: `${meta.accent}19` }]}>
          <Ionicons name={meta.icon} size={20} color={meta.accent} />
        </View>
        <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1, textTransform: 'capitalize' }]}>
          {meta.label}
        </Text>
        <View style={[styles.countPill, { backgroundColor: `${meta.accent}1F` }]}>
          <Text style={[theme.typography.caption, { color: meta.accent, fontWeight: '700' }]}>{count}</Text>
        </View>
        <Ionicons name="chevron-forward" size={17} color={theme.colors.textMuted} />
      </View>
    </MotionPressable>
  );
}
function ManagerAction({
  icon,
  title,
  subtitle,
  accent,
  onPress,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  title: string;
  subtitle: string;
  accent: string;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  return (
    <MotionPressable
      onPress={onPress}
      haptic="selection"
      style={styles.quickShell}
      contentStyle={styles.rounded}
      accessibilityRole="button"
      accessibilityLabel={`${title}. ${subtitle}`}
    >
      <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.quickSurface} contentStyle={styles.quickCard}>
        <View style={[styles.quickIcon, { backgroundColor: `${accent}19` }]}>
          <Ionicons name={icon} size={23} color={accent} />
        </View>
        <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, marginTop: 12 }]}>{title}</Text>
        <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 3 }]}>{subtitle}</Text>
      </GlassSurface>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  heroActions: { flexDirection: 'row', gap: 8 },
  profileAvatarButton: { width: 44, height: 44, borderRadius: 15 },
  root: { flex: 1 },
  content: { paddingBottom: 34 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  deckSection: { marginTop: 16 },
  deckPage: { flex: 1, padding: 16 },
  deckHeading: { flexDirection: 'row', alignItems: 'center', gap: 10, marginBottom: 12 },
  deckHeadingCopy: { flex: 1, minWidth: 0 },
  deckIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  deckLink: { minHeight: 44, paddingHorizontal: 8, alignItems: 'center', justifyContent: 'center' },
  deckCount: { minWidth: 38, minHeight: 38, borderRadius: 14, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 9 },
  deckAlert: { minHeight: 50, borderRadius: 16, flexDirection: 'row', alignItems: 'center', gap: 9, paddingHorizontal: 12 },
  deckApprovalHero: { minHeight: 76, borderRadius: 18, flexDirection: 'row', alignItems: 'center', gap: 11, padding: 12 },
  deckApprovalList: { marginTop: 10, borderTopWidth: StyleSheet.hairlineWidth },
  deckClear: { minHeight: 100, alignItems: 'center', justifyContent: 'center', gap: 7 },
  deckOvertimeShell: { marginTop: 10 },
  deckOvertime: { minHeight: 50, borderRadius: 16, flexDirection: 'row', alignItems: 'center', gap: 9, paddingHorizontal: 12 },
  deckEmpty: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 8 },
  attendanceLoadingCard: { minHeight: 156, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 22 },
  employeeLinkCard: { flexDirection: 'row', alignItems: 'flex-start', gap: 14, padding: 18 },
  employeeLinkIcon: { width: 48, height: 48, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  employeeLinkCopy: { flex: 1 },
  stateCard: { minHeight: 190, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  retryButton: { minHeight: 44, paddingHorizontal: 20, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  rounded: { flex: 1, borderRadius: 24 },
  approvalBanner: { flexDirection: 'row', alignItems: 'center', gap: 13, padding: 16 },
  approvalIcon: { width: 48, height: 48, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  approvalCopy: { flex: 1 },
  metricGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  metricCard: { width: '48%', minHeight: 104 },
  metricContent: { padding: 12, justifyContent: 'space-between' },
  metricIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  metricValue: { fontSize: 26, lineHeight: 31, fontWeight: '800', letterSpacing: -0.4, marginTop: 4 },
  lateShell: { marginTop: 10 },
  lateAlert: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 14 },
  listCard: { paddingHorizontal: 14 },
  approvalRow: { minHeight: 68, flexDirection: 'row', alignItems: 'center', gap: 11, paddingVertical: 10 },
  approvalRowIcon: { width: 42, height: 42, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  countPill: { minWidth: 30, height: 28, borderRadius: 14, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 8 },
  quickGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  quickShell: { width: '48%', minHeight: 108 },
  quickSurface: { flex: 1 },
  quickCard: { flex: 1, padding: 12 },
  quickIcon: { width: 43, height: 43, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  overtimeCard: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', padding: 18 },
  overtimeValue: { fontSize: 32, lineHeight: 38, fontWeight: '800', letterSpacing: -0.7, marginVertical: 5 },
  overtimeIcon: { width: 58, height: 58, borderRadius: 20, alignItems: 'center', justifyContent: 'center' },
  bottomSpacer: { height: 14 },
});
