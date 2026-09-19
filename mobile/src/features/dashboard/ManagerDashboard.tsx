import React, { useCallback, useEffect, useState } from 'react';
import {
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useAuthStore } from '@/auth/authStore';
import { dashboardApi } from '@/api/services';
import { navigateTo, isManagerUser, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { ManagerDashboard } from '@/types';

interface Props {
  navigation: any;
}

export default function ManagerDashboardScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const [dashboard, setDashboard] = useState<ManagerDashboard | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async () => {
    try {
      setDashboard(await dashboardApi.getManagerDashboard());
    } catch (error) {
      console.error('[ManagerDashboard]', error);
    } finally {
      setRefreshing(false);
    }
  }, []);
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
            <GlassIconButton
              icon="notifications-outline"
              label="Notifications"
              onPress={() => go('Notifications')}
            />
          }
        />
        {pendingCount > 0 ? (
          <View style={styles.section}>
            <MotionPressable
              onPress={() => go('Approvals')}
              haptic="medium"
              contentStyle={styles.rounded}
            >
              <GlassSurface
                radius={theme.radius.xl}
                tintColor={theme.isDark ? 'rgba(41,83,180,0.30)' : 'rgba(255,255,255,0.52)'}
                contentStyle={styles.approvalBanner}
              >
                <View style={[styles.approvalIcon, { backgroundColor: `${theme.colors.primary}20` }]}>
                  <Ionicons name="checkmark-done-outline" size={24} color={theme.colors.primary} />
                </View>
                <View style={styles.approvalCopy}>
                  <Text style={[theme.typography.h3, { color: theme.colors.text }]}>
                    {pendingCount} approval{pendingCount === 1 ? '' : 's'} waiting
                  </Text>
                  <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 3 }]}>
                    Review decisions that need your attention
                  </Text>
                </View>
                <Ionicons name="arrow-forward" size={20} color={theme.colors.primary} />
              </GlassSurface>
            </MotionPressable>
          </View>
        ) : null}

        {team ? (
          <View style={styles.section}>
            <SectionHeader title="Team today" subtitle="Live workforce snapshot" />
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
                <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.lateAlert}>
                  <Ionicons name="warning-outline" size={20} color={theme.colors.warning} />
                  <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>
                    {team.lateToday} employee{team.lateToday === 1 ? '' : 's'} arrived late
                  </Text>
                  <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>View</Text>
                </GlassSurface>
              </MotionPressable>
            ) : null}
          </View>
        ) : null}
        {dashboard?.pendingByType && Object.values(dashboard.pendingByType).some((count) => count > 0) ? (
          <View style={styles.section}>
            <SectionHeader
              title="Approvals by type"
              subtitle="Prioritize the queue"
              actionLabel="View all"
              onAction={() => go('Approvals')}
            />
            <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.listCard}>
              {Object.entries(dashboard.pendingByType)
                .filter(([, count]) => count > 0)
                .map(([type, count], index, items) => (
                  <ApprovalTypeRow
                    key={type}
                    type={type}
                    count={count}
                    isLast={index === items.length - 1}
                    onPress={() => go('Approvals', { filterType: type })}
                  />
                ))}
            </GlassSurface>
          </View>
        ) : null}

        <View style={styles.section}>
          <SectionHeader title="Manager shortcuts" subtitle="Move work forward" />
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
        </View>
        {dashboard?.overtimeAlert && dashboard.overtimeAlert.thisMonth > 0 ? (
          <View style={styles.section}>
            <MotionPressable
              onPress={() => go('Approvals', { filterType: 'OVERTIME' })}
              haptic="selection"
              contentStyle={styles.rounded}
            >
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.overtimeCard}>
                <View>
                  <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>Overtime this month</Text>
                  <Text style={[styles.overtimeValue, { color: theme.colors.text }]}>
                    {dashboard.overtimeAlert.thisMonth}h
                  </Text>
                  <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                    {dashboard.overtimeAlert.lastMonth}h last month
                  </Text>
                </View>
                <View style={[styles.overtimeIcon, { backgroundColor: `${theme.colors.warning}1E` }]}>
                  <Ionicons name="time-outline" size={30} color={theme.colors.warning} />
                </View>
              </GlassSurface>
            </MotionPressable>
          </View>
        ) : null}

        <View style={styles.bottomSpacer} />
      </ScrollView>
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
  root: { flex: 1 },
  content: { paddingBottom: 34 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  rounded: { flex: 1, borderRadius: 24 },
  approvalBanner: { flexDirection: 'row', alignItems: 'center', gap: 13, padding: 16 },
  approvalIcon: { width: 48, height: 48, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  approvalCopy: { flex: 1 },
  metricGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  metricCard: { width: '48%', minHeight: 132 },
  metricContent: { padding: 15, justifyContent: 'space-between' },
  metricIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  metricValue: { fontSize: 31, lineHeight: 36, fontWeight: '800', letterSpacing: -0.6, marginTop: 8 },
  lateShell: { marginTop: 10 },
  lateAlert: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 14 },
  listCard: { paddingHorizontal: 14 },
  approvalRow: { minHeight: 68, flexDirection: 'row', alignItems: 'center', gap: 11, paddingVertical: 10 },
  approvalRowIcon: { width: 42, height: 42, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  countPill: { minWidth: 30, height: 28, borderRadius: 14, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 8 },
  quickGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  quickShell: { width: '48%', minHeight: 128 },
  quickSurface: { flex: 1 },
  quickCard: { flex: 1, padding: 15 },
  quickIcon: { width: 43, height: 43, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  overtimeCard: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', padding: 18 },
  overtimeValue: { fontSize: 32, lineHeight: 38, fontWeight: '800', letterSpacing: -0.7, marginVertical: 5 },
  overtimeIcon: { width: 58, height: 58, borderRadius: 20, alignItems: 'center', justifyContent: 'center' },
  bottomSpacer: { height: 14 },
});
