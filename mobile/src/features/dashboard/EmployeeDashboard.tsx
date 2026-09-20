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
import Animated, {
  useAnimatedStyle,
  useSharedValue,
  withSpring,
} from 'react-native-reanimated';
import * as Location from 'expo-location';
import { useTranslation } from 'react-i18next';
import { AttendanceSelfieModal } from '@/features/attendance/AttendanceSelfieModal';
import { useAuthStore } from '@/auth/authStore';
import { attendanceApi, dashboardApi } from '@/api/services';
import { getDeviceInfo } from '@/utils/device';
import { daysUntil, formatDate, formatTime } from '@/utils/date';
import { navigateTo, isManagerUser, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import {
  EmployeeAvatar,
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SwipeDeck,
} from '@/components/ui';
import type {
  EmployeeDashboard,
  GeoLocation,
  LeaveBalance,
  PunchType,
  TodayAttendance,
} from '@/types';

interface Props {
  navigation: any;
}

export default function EmployeeDashboardScreen({ navigation }: Props) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const { user } = useAuthStore();
  const [dashboard, setDashboard] = useState<EmployeeDashboard | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [punchLoading, setPunchLoading] = useState(false);
  const [pendingPunchType, setPendingPunchType] = useState<PunchType | null>(null);
  const [punchFeedback, setPunchFeedback] = useState<PunchType | null>(null);
  const loadDashboard = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    setLoadError(null);
    try {
      setDashboard(await dashboardApi.getEmployeeDashboard());
    } catch (error) {
      console.error('[Dashboard] Load error:', error);
      setLoadError('We couldn’t load your dashboard. Check your connection and try again.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void loadDashboard();
  }, [loadDashboard]);

  const onRefresh = useCallback(() => {
    setRefreshing(true);
    void loadDashboard(true);
  }, [loadDashboard]);

  const submitPunch = useCallback(async (
    punchType: PunchType,
    selfiePhotoReference?: string,
    verification?: { deviceFaceVerified: boolean; faceCapabilityAvailable: boolean },
  ) => {
    setPunchLoading(true);
    try {
      const { status } = await Location.requestForegroundPermissionsAsync();
      if (status !== 'granted') {
        Alert.alert('Location required', t('attendance.locationRequired'));
        return;
      }

      const location = await Location.getCurrentPositionAsync({
        accuracy: Location.Accuracy.High,
      });
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
      await loadDashboard(true);
      setPunchFeedback(punchType);
    } catch (error: any) {
      Alert.alert(
        'Could not record attendance',
        error?.response?.data?.message ?? 'Please check your connection and try again.',
      );
      throw error;
    } finally {
      setPunchLoading(false);
    }
  }, [loadDashboard, t]);

  const handlePunch = useCallback((punchType: PunchType) => {
    setPendingPunchType(punchType);
  }, []);

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
      // submitPunch shows the user-facing error and the selfie modal remains open for retry.
    }
  }, [pendingPunchType, submitPunch]);
  const attendance = dashboard?.todayAttendance;
  const canClockIn = !attendance?.currentlyActive;
  const canClockOut = !!attendance?.currentlyActive;
  const firstName = user?.fullName?.split(' ')[0] ?? 'there';
  const greeting = getGreeting();
  const go = (route: AppRoute, params?: Record<string, unknown>) =>
    navigateTo(navigation, route, isManagerUser(user), params);
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
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={greeting}
          title={`${firstName}, your day is ready`}
          subtitle={`${formatDate(new Date().toISOString(), 'date')} · ${new Date().toLocaleDateString('en-US', { weekday: 'long' })}`}
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
                  name={dashboard?.profile?.fullName ?? user?.fullName ?? 'Employee'}
                  photoUrl={dashboard?.profile?.profilePhotoUrl ?? user?.profilePhotoUrl}
                  size={44}
                  ring
                />
              </MotionPressable>
              <GlassIconButton
                icon="notifications-outline"
                label="Notifications"
                badge={dashboard?.unreadNotifications}
                onPress={() => go('Notifications')}
              />
              <GlassIconButton
                icon="sparkles-outline"
                label="AI Assistant"
                accent
                onPress={() => go('AIAssistant')}
              />
            </View>
          }
        />

        {loading && !dashboard ? (
          <LoadingDashboard />
        ) : loadError ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.errorCard}>
              <Ionicons name="cloud-offline-outline" size={28} color={theme.colors.danger} />
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Dashboard unavailable</Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, textAlign: 'center' }]}>{loadError}</Text>
              <LiquidButton label="Try again" onPress={() => void loadDashboard()} />
            </GlassSurface>
          </View>
        ) : (
          <>
            <View style={styles.section}>
              <AttendanceCard
                attendance={attendance}
                canClockIn={canClockIn}
                canClockOut={canClockOut}
                punchLoading={punchLoading}
                onClockIn={() => void handlePunch('CLOCK_IN')}
                onClockOut={() => void handlePunch('CLOCK_OUT')}
                onViewHistory={() => go('AttendanceHistory')}
                feedback={punchFeedback}
              />
            </View>
            <View style={styles.deckSection}>
              <SectionHeader
                title="Your workspace"
                subtitle="Swipe between actions, balances and updates"
              />
              <SwipeDeck minHeight={322}>
                <GlassSurface
                  elevated={false}
                  radius={theme.radius.xl}
                  contentStyle={styles.deckPage}
                >
                  <View style={styles.deckHeading}>
                    <View style={[styles.deckIcon, { backgroundColor: theme.colors.primary + '18' }]}>
                      <Ionicons name="flash-outline" size={21} color={theme.colors.primary} />
                    </View>
                    <View style={styles.deckHeadingCopy}>
                      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Quick actions</Text>
                      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                        The tasks you use most
                      </Text>
                    </View>
                  </View>
                  <View style={styles.actionGrid}>
                    <QuickAction
                      icon="calendar-outline"
                      title="Apply leave"
                      subtitle="Time off request"
                      accent={theme.colors.primary}
                      onPress={() => go('ApplyLeave')}
                    />
                    <QuickAction
                      icon="time-outline"
                      title="Overtime"
                      subtitle="Submit hours"
                      accent={theme.colors.violet}
                      onPress={() => go('Overtime')}
                    />
                    <QuickAction
                      icon="wallet-outline"
                      title="Payslips"
                      subtitle="Salary history"
                      accent={theme.colors.success}
                      onPress={() => go('Payslips')}
                    />
                    <QuickAction
                      icon="chatbubble-ellipses-outline"
                      title="HR request"
                      subtitle="Get support"
                      accent={theme.colors.warning}
                      onPress={() => go('HRRequests')}
                    />
                  </View>
                </GlassSurface>

                <GlassSurface
                  elevated={false}
                  radius={theme.radius.xl}
                  contentStyle={styles.deckPage}
                >
                  <View style={styles.deckHeading}>
                    <View style={[styles.deckIcon, { backgroundColor: theme.colors.success + '18' }]}>
                      <Ionicons name="calendar-clear-outline" size={21} color={theme.colors.success} />
                    </View>
                    <View style={styles.deckHeadingCopy}>
                      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Time off</Text>
                      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                        Balances and pending requests
                      </Text>
                    </View>
                    <MotionPressable
                      accessibilityRole="button"
                      accessibilityLabel="Open leave"
                      onPress={() => go('ApplyLeave')}
                      haptic="selection"
                      contentStyle={styles.deckLink}
                    >
                      <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
                        Open
                      </Text>
                    </MotionPressable>
                  </View>

                  {(dashboard?.leaveBalances?.length ?? 0) > 0 ? (
                    <View style={styles.deckLeaveRow}>
                      {dashboard!.leaveBalances.slice(0, 2).map((balance) => (
                        <LeaveBalanceCard key={balance.leaveTypeId} balance={balance} />
                      ))}
                    </View>
                  ) : (
                    <View style={styles.deckEmpty}>
                      <Ionicons name="calendar-outline" size={26} color={theme.colors.textMuted} />
                      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                        No leave balances available yet.
                      </Text>
                    </View>
                  )}

                  {(dashboard?.pendingRequestsCount ?? 0) > 0 ? (
                    <MotionPressable
                      onPress={() => go('HRRequests')}
                      haptic="selection"
                      contentStyle={styles.fullRadius}
                    >
                      <View style={[styles.deckPendingRow, { backgroundColor: theme.colors.surfaceSoft }]}>
                        <View style={[styles.pendingIcon, { backgroundColor: theme.colors.warning + '1F' }]}>
                          <Ionicons name="hourglass-outline" size={20} color={theme.colors.warning} />
                        </View>
                        <View style={styles.pendingCopy}>
                          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                            {dashboard!.pendingRequestsCount} pending request
                            {dashboard!.pendingRequestsCount === 1 ? '' : 's'}
                          </Text>
                          <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                            Tap to review status
                          </Text>
                        </View>
                        <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
                      </View>
                    </MotionPressable>
                  ) : null}
                </GlassSurface>

                <GlassSurface
                  elevated={false}
                  radius={theme.radius.xl}
                  contentStyle={styles.deckPage}
                >
                  <View style={styles.deckHeading}>
                    <View style={[styles.deckIcon, { backgroundColor: theme.colors.violet + '18' }]}>
                      <Ionicons name="wallet-outline" size={21} color={theme.colors.violet} />
                    </View>
                    <View style={styles.deckHeadingCopy}>
                      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Pay & documents</Text>
                      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                        Latest payroll and document alerts
                      </Text>
                    </View>
                  </View>

                  {dashboard?.latestPayslip ? (
                    <MotionPressable
                      onPress={() => go('PayslipDetail', { id: dashboard.latestPayslip!.id })}
                      haptic="selection"
                      contentStyle={styles.fullRadius}
                    >
                      <View style={[styles.deckPayslip, { backgroundColor: theme.colors.surfaceSoft }]}>
                        <View style={styles.deckPayslipCopy}>
                          <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>NET SALARY</Text>
                          <Text style={[styles.deckPayAmount, { color: theme.colors.text }]}>
                            {dashboard.latestPayslip.currency}{' '}
                            {dashboard.latestPayslip.netSalary.toLocaleString('en-US', {
                              minimumFractionDigits: 2,
                            })}
                          </Text>
                          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                            {dashboard.latestPayslip.periodLabel}
                          </Text>
                        </View>
                        <View style={[styles.payslipArrow, { backgroundColor: theme.colors.primary + '18' }]}>
                          <Ionicons name="arrow-forward" size={20} color={theme.colors.primary} />
                        </View>
                      </View>
                    </MotionPressable>
                  ) : null}

                  {(dashboard?.expiringDocuments?.length ?? 0) > 0 ? (
                    <View style={[styles.deckDocs, { borderTopColor: theme.colors.divider }]}>
                      {dashboard!.expiringDocuments.slice(0, 2).map((document, index, items) => (
                        <DocumentAlert
                          key={document.id}
                          name={document.documentType}
                          expiryDate={document.expiryDate}
                          isLast={index === items.length - 1}
                          onPress={() => go('Documents')}
                        />
                      ))}
                    </View>
                  ) : (
                    <View style={styles.deckEmpty}>
                      <Ionicons name="checkmark-circle-outline" size={26} color={theme.colors.success} />
                      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                        No document alerts need your attention.
                      </Text>
                    </View>
                  )}
                </GlassSurface>
              </SwipeDeck>
            </View>
          </>
        )}

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

function getGreeting() {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 17) return 'Good afternoon';
  return 'Good evening';
}
function LoadingDashboard() {
  const { theme } = useTheme();
  return (
    <View style={styles.section}>
      <GlassSurface radius={theme.radius.xl} contentStyle={styles.loadingCard}>
        <ActivityIndicator color={theme.colors.primary} size="small" />
        <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
          Loading your live workforce data…
        </Text>
      </GlassSurface>
    </View>
  );
}

export interface AttendanceCardProps {
  attendance?: TodayAttendance;
  canClockIn: boolean;
  canClockOut: boolean;
  punchLoading: boolean;
  onClockIn: () => void;
  onClockOut: () => void;
  onViewHistory: () => void;
  feedback: PunchType | null;
}

export function AttendanceCard({
  attendance,
  canClockIn,
  canClockOut,
  punchLoading,
  onClockIn,
  onClockOut,
  onViewHistory,
  feedback,
}: AttendanceCardProps) {
  const { theme, reduceMotion } = useTheme();
  const feedbackProgress = useSharedValue(feedback ? 1 : 0);
  useEffect(() => {
    if (!feedback) {
      feedbackProgress.value = 0;
      return;
    }
    feedbackProgress.value = reduceMotion
      ? 1
      : withSpring(1, { damping: 18, stiffness: 260 });
  }, [feedback, feedbackProgress, reduceMotion]);
  const feedbackStyle = useAnimatedStyle(() => ({
    opacity: feedbackProgress.value,
    transform: [{ scale: reduceMotion ? 1 : 0.94 + (feedbackProgress.value * 0.06) }],
  }));
  const statusColor = getAttendanceStatusColor(attendance?.status, theme);
  const statusText = attendance?.status?.replaceAll('_', ' ') ?? 'Not recorded';

  return (
    <GlassSurface
      radius={theme.radius.xxl}
      tintColor={theme.isDark ? 'rgba(23,61,112,0.26)' : 'rgba(255,255,255,0.50)'}
      contentStyle={styles.attendanceCard}
    >
      <View style={styles.attendanceTop}>
        <View>
          <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Today’s attendance</Text>
          <View style={[styles.statusPill, { backgroundColor: `${statusColor}1E`, marginTop: 8 }]}>
            <View style={[styles.statusDot, { backgroundColor: statusColor }]} />
            <Text style={[theme.typography.micro, { color: statusColor, textTransform: 'capitalize' }]}>
              {statusText.toLowerCase()}
            </Text>
          </View>
        </View>
        <MotionPressable
          onPress={onViewHistory}
          haptic="selection"
          contentStyle={styles.historyButton}
        >
          <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
            History
          </Text>
          <Ionicons name="arrow-forward" size={15} color={theme.colors.primary} />
        </MotionPressable>
      </View>

      <View style={[styles.timeRail, { backgroundColor: theme.colors.surfaceSoft }]}>
        <TimeMetric
          icon="log-in-outline"
          label="Clock in"
          value={attendance?.clockIn ? formatTime(attendance.clockIn) : '—'}
          color={theme.colors.success}
        />
        <View style={[styles.timeDivider, { backgroundColor: theme.colors.divider }]} />
        <TimeMetric
          icon="log-out-outline"
          label="Clock out"
          value={attendance?.clockOut ? formatTime(attendance.clockOut) : '—'}
          color={theme.colors.danger}
        />
        {attendance?.shiftStart ? (
          <>
            <View style={[styles.timeDivider, { backgroundColor: theme.colors.divider }]} />
            <TimeMetric
              icon="time-outline"
              label="Shift"
              value={`${formatTime(attendance.shiftStart)} – ${formatTime(attendance.shiftEnd)}`}
              color={theme.colors.primary}
            />
          </>
        ) : null}
      </View>

      {attendance?.workLocation ? (
        <View style={styles.locationRow}>
          <Ionicons name="location-outline" size={16} color={theme.colors.textMuted} />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, flex: 1 }]}>
            {attendance.workLocation}
          </Text>
        </View>
      ) : null}

      {feedback ? (
        <Animated.View
          accessible
          accessibilityLiveRegion="polite"
          style={[styles.attendanceFeedback, { backgroundColor: `${theme.colors.success}18` }, feedbackStyle]}
        >
          <Ionicons name="checkmark-circle" size={20} color={theme.colors.success} />
          <View style={styles.feedbackCopy}>
            <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>Attendance updated</Text>
            <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
              {feedback === 'CLOCK_IN' ? 'You’re clocked in and your shift is active.' : 'You’re clocked out and today’s hours are recorded.'}
            </Text>
          </View>
        </Animated.View>
      ) : null}

      <View style={styles.punchRow}>
        {canClockIn ? (
          <LiquidButton
            label="Clock in"
            icon="log-in-outline"
            onPress={onClockIn}
            loading={punchLoading}
            disabled={punchLoading}
            variant="success"
            style={styles.flexButton}
          />
        ) : null}
        {canClockOut ? (
          <LiquidButton
            label="Clock out"
            icon="log-out-outline"
            onPress={onClockOut}
            loading={punchLoading}
            disabled={punchLoading}
            variant="danger"
            style={styles.flexButton}
          />
        ) : null}
      </View>
    </GlassSurface>
  );
}

function TimeMetric({
  icon,
  label,
  value,
  color,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  label: string;
  value: string;
  color: string;
}) {
  const { theme } = useTheme();
  return (
    <View style={styles.timeMetric}>
      <Ionicons name={icon} size={17} color={color} />
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{label}</Text>
      <Text numberOfLines={1} style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
        {value}
      </Text>
    </View>
  );
}

function getAttendanceStatusColor(
  status: TodayAttendance['status'] | undefined,
  theme: ReturnType<typeof useTheme>['theme'],
) {
  const colors: Record<string, string> = {
    PRESENT: theme.colors.success,
    ABSENT: theme.colors.danger,
    LATE: theme.colors.warning,
    HALF_DAY: theme.colors.warning,
    ON_LEAVE: theme.colors.primary,
    HOLIDAY: theme.colors.violet,
    WEEKEND: theme.colors.textMuted,
    MISSING_PUNCH: theme.colors.danger,
  };
  return colors[status ?? ''] ?? theme.colors.textMuted;
}
function QuickAction({
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
      accessibilityRole="button"
      accessibilityLabel={`${title}. ${subtitle}`}
      onPress={onPress}
      haptic="selection"
      style={styles.actionShell}
      contentStyle={styles.fullRadius}
    >
      <GlassSurface
        elevated={false}
        radius={theme.radius.xl}
        contentStyle={styles.actionCard}
        style={styles.actionSurface}
      >
        <View style={[styles.actionIcon, { backgroundColor: `${accent}19` }]}>
          <Ionicons name={icon} size={23} color={accent} />
        </View>
        <View style={styles.actionCopy}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{title}</Text>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 3 }]}>
            {subtitle}
          </Text>
        </View>
        <Ionicons name="arrow-up-outline" size={16} color={theme.colors.textMuted} style={styles.actionArrow} />
      </GlassSurface>
    </MotionPressable>
  );
}

function LeaveBalanceCard({ balance }: { balance: LeaveBalance }) {
  const { theme } = useTheme();
  const usage = balance.allocated > 0
    ? Math.min(100, Math.max(0, (balance.available / balance.allocated) * 100))
    : 0;

  return (
    <GlassSurface
      elevated={false}
      radius={theme.radius.xl}
      style={styles.leaveCard}
      contentStyle={styles.leaveContent}
    >
      <Text numberOfLines={1} style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
        {balance.leaveTypeName}
      </Text>
      <View style={styles.leaveValueRow}>
        <Text style={[styles.leaveValue, { color: theme.colors.text }]}>{balance.available}</Text>
        <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginBottom: 4 }]}>
          {balance.unit.toLowerCase()} left
        </Text>
      </View>
      <View style={[styles.progressTrack, { backgroundColor: theme.colors.divider }]}>
        <View
          style={[
            styles.progressFill,
            { width: `${usage}%`, backgroundColor: theme.colors.primary },
          ]}
        />
      </View>
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 8 }]}>
        {balance.used} used · {balance.pending} pending
      </Text>
    </GlassSurface>
  );
}

function DocumentAlert({
  name,
  expiryDate,
  isLast,
  onPress,
}: {
  name: string;
  expiryDate?: string;
  isLast: boolean;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const days = expiryDate ? daysUntil(expiryDate) : null;
  const color = days === null
    ? theme.colors.textMuted
    : days <= 0
      ? theme.colors.danger
      : days <= 30
        ? theme.colors.warning
        : theme.colors.success;
  const subtitle = days === null
    ? 'Expiry date unavailable'
    : days <= 0
      ? 'Expired'
      : days === 1
        ? 'Expires tomorrow'
        : `Expires in ${days} days`;

  return (
    <MotionPressable onPress={onPress} haptic="selection" contentStyle={styles.fullRadius}>
      <View
        style={[
          styles.documentRow,
          !isLast && { borderBottomColor: theme.colors.divider, borderBottomWidth: StyleSheet.hairlineWidth },
        ]}
      >
        <View style={[styles.documentDot, { backgroundColor: color }]} />
        <View style={styles.documentCopy}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{name}</Text>
          <Text style={[theme.typography.caption, { color, marginTop: 2 }]}>{subtitle}</Text>
        </View>
        <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
      </View>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flex: 1, backgroundColor: 'transparent' },
  content: { paddingBottom: 34 },
  heroActions: { flexDirection: 'row', gap: 8 },
  profileAvatarButton: { width: 44, height: 44, borderRadius: 15 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  deckSection: { marginTop: 16 },
  deckPage: { flex: 1, padding: 16 },
  deckHeading: { flexDirection: 'row', alignItems: 'center', gap: 10, marginBottom: 14 },
  deckHeadingCopy: { flex: 1, minWidth: 0 },
  deckIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  deckLink: { minHeight: 44, paddingHorizontal: 8, alignItems: 'center', justifyContent: 'center' },
  deckLeaveRow: { flexDirection: 'row', gap: 10, marginBottom: 10 },
  deckPendingRow: {
    minHeight: 64,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    paddingHorizontal: 12,
    borderRadius: 18,
  },
  deckPayslip: {
    minHeight: 108,
    borderRadius: 18,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    padding: 14,
  },
  deckPayslipCopy: { flex: 1, minWidth: 0 },
  deckPayAmount: { fontSize: 25, lineHeight: 30, fontWeight: '800', marginVertical: 3 },
  deckDocs: { marginTop: 10, borderTopWidth: StyleSheet.hairlineWidth },
  deckEmpty: { flex: 1, minHeight: 160, alignItems: 'center', justifyContent: 'center', gap: 8 },
  errorCard: { minHeight: 190, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  loadingCard: {
    minHeight: 150,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
    padding: 20,
  },
  attendanceCard: { padding: 18 },
  attendanceTop: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    gap: 12,
  },
  historyButton: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    paddingHorizontal: 8,
    paddingVertical: 7,
    borderRadius: 12,
  },
  statusPill: {
    alignSelf: 'flex-start',
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 999,
  },
  statusDot: { width: 7, height: 7, borderRadius: 4 },
  timeRail: {
    flexDirection: 'row',
    alignItems: 'stretch',
    borderRadius: 18,
    paddingVertical: 13,
    paddingHorizontal: 8,
    marginTop: 18,
  },
  timeMetric: { flex: 1, alignItems: 'center', gap: 3, minWidth: 0 },
  timeDivider: { width: StyleSheet.hairlineWidth, marginHorizontal: 6 },
  locationRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 7,
    marginTop: 12,
    paddingHorizontal: 2,
  },
  punchRow: { flexDirection: 'row', gap: 10, marginTop: 17 },
  attendanceFeedback: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 12, borderRadius: 16, marginTop: 14 },
  feedbackCopy: { flex: 1, gap: 2 },
  flexButton: { flex: 1 },
  actionGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  actionShell: { width: '48%', minHeight: 118 },
  actionSurface: { flex: 1 },
  actionCard: { flex: 1, padding: 14 },
  actionIcon: {
    width: 42,
    height: 42,
    borderRadius: 15,
    alignItems: 'center',
    justifyContent: 'center',
  },
  actionCopy: { marginTop: 12 },
  actionArrow: { position: 'absolute', top: 14, right: 14, transform: [{ rotate: '45deg' }] },
  fullRadius: { flex: 1, borderRadius: 24 },
  horizontalRail: { gap: 10, paddingRight: 4 },
  leaveCard: { width: 164, minHeight: 150 },
  leaveContent: { padding: 15 },
  leaveValueRow: { flexDirection: 'row', alignItems: 'flex-end', gap: 6, marginTop: 8 },
  leaveValue: { fontSize: 31, lineHeight: 36, fontWeight: '800', letterSpacing: -0.6 },
  progressTrack: { height: 5, borderRadius: 999, overflow: 'hidden', marginTop: 12 },
  progressFill: { height: '100%', borderRadius: 999 },
  pendingCard: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    padding: 14,
  },
  pendingIcon: {
    width: 44,
    height: 44,
    borderRadius: 15,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pendingCopy: { flex: 1 },
  listCard: { paddingHorizontal: 14 },
  documentRow: {
    minHeight: 70,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 12,
  },
  documentDot: { width: 10, height: 10, borderRadius: 5 },
  documentCopy: { flex: 1 },
  payslipCard: { padding: 18 },
  payslipTop: { flexDirection: 'row', alignItems: 'flex-start', justifyContent: 'space-between', gap: 12 },
  payslipAmount: { fontSize: 30, lineHeight: 36, fontWeight: '800', letterSpacing: -0.7, marginTop: 5 },
  payslipDivider: { height: StyleSheet.hairlineWidth, marginVertical: 16 },
  payslipBottom: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' },
  payslipArrow: { width: 42, height: 42, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  holidayRow: { minHeight: 76, flexDirection: 'row', alignItems: 'center', gap: 13, paddingVertical: 11 },
  holidayDate: { width: 48, height: 51, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  holidayDay: { fontSize: 17, lineHeight: 20, fontWeight: '800' },
  holidayCopy: { flex: 1 },
  bottomSpacer: { height: 14 },
});
