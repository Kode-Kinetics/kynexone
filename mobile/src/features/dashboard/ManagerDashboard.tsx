// ============================================================
// ZAYRA MOBILE — Manager Dashboard Screen
// ============================================================

import React, { useEffect, useState, useCallback } from 'react';
import {
  View,
  Text,
  ScrollView,
  TouchableOpacity,
  RefreshControl,
  StyleSheet,
  Platform,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import { useAuthStore } from '@/auth/authStore';
import { dashboardApi } from '@/api/services';
import { COLORS } from '@/config';
import { navigateTo, isManagerUser, type AppRoute } from '@/navigation/routes';
import type { ManagerDashboard } from '@/types';

interface Props {
  navigation: any;
}

export default function ManagerDashboardScreen({ navigation }: Props) {
  const { user } = useAuthStore();
  const [dashboard, setDashboard] = useState<ManagerDashboard | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async () => {
    try {
      const data = await dashboardApi.getManagerDashboard();
      setDashboard(data);
    } catch (err) {
      console.error('[ManagerDashboard]', err);
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
  const firstName = user?.fullName?.split(' ')[0] ?? '';
  const go = (route: AppRoute, params?: Record<string, unknown>) =>
    navigateTo(navigation, route, isManagerUser(user), params);
  const team = dashboard?.teamSummary;
  const pendingCount = dashboard?.pendingApprovalsCount ?? 0;

  return (
    <ScrollView
      style={styles.container}
      contentContainerStyle={styles.content}
      refreshControl={
        <RefreshControl refreshing={refreshing} onRefresh={onRefresh} tintColor={COLORS.blue} />
      }
      showsVerticalScrollIndicator={false}
    >
      {/* Header */}
      <LinearGradient colors={['#0B1020', '#0F1E40']} style={styles.header}>
        <View style={styles.headerRow}>
          <View>
            <Text style={styles.greeting}>Manager View</Text>
            <Text style={styles.name}>{firstName}</Text>
          </View>
          <View style={styles.headerActions}>
            <TouchableOpacity
              style={styles.headerIcon}
              onPress={() => go('Notifications')}
            >
              <Ionicons name="notifications-outline" size={22} color="#fff" />
            </TouchableOpacity>
          </View>
        </View>
        <Text style={styles.dateText}>
          {new Date().toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
        </Text>
      </LinearGradient>

      {/* Pending Approvals banner */}
      {pendingCount > 0 && (
        <TouchableOpacity
          style={styles.approvalBanner}
          onPress={() => go('Approvals')}
        >
          <LinearGradient
            colors={['#2F6BFF', '#1A4FCC']}
            style={styles.approvalBannerInner}
            start={{ x: 0, y: 0 }} end={{ x: 1, y: 0 }}
          >
            <Ionicons name="checkmark-circle-outline" size={22} color="#fff" />
            <View style={{ flex: 1 }}>
              <Text style={styles.approvalBannerTitle}>{pendingCount} Pending Approvals</Text>
              <Text style={styles.approvalBannerSub}>Tap to review and action</Text>
            </View>
            <Ionicons name="arrow-forward" size={18} color="#fff" />
          </LinearGradient>
        </TouchableOpacity>
      )}

      {/* Team Summary */}
      {team && (
        <View style={styles.section}>
          <Text style={styles.sectionTitle}>Team Today</Text>
          <View style={styles.teamGrid}>
            <TeamStatCard
              label="Total"
              value={team.total}
              color={COLORS.blue}
              icon="people-outline"
            />
            <TeamStatCard
              label="Present"
              value={team.present}
              color={COLORS.success}
              icon="checkmark-circle-outline"
            />
            <TeamStatCard
              label="Absent"
              value={team.absent}
              color={COLORS.error}
              icon="close-circle-outline"
            />
            <TeamStatCard
              label="On Leave"
              value={team.onLeave}
              color="#7C3AED"
              icon="airplane-outline"
            />
          </View>
          {team.lateToday > 0 && (
            <TouchableOpacity
              style={styles.lateAlert}
              onPress={() => go('Team')}
            >
              <Ionicons name="warning-outline" size={16} color={COLORS.warning} />
              <Text style={styles.lateAlertText}>
                {team.lateToday} employee{team.lateToday !== 1 ? 's' : ''} late today
              </Text>
              <Text style={styles.lateAlertLink}>View →</Text>
            </TouchableOpacity>
          )}
        </View>
      )}

      {/* Approval breakdown */}
      {dashboard?.pendingByType && Object.keys(dashboard.pendingByType).length > 0 && (
        <View style={styles.section}>
          <View style={styles.sectionHeader}>
            <Text style={styles.sectionTitle}>Approvals by Type</Text>
            <TouchableOpacity onPress={() => go('Approvals')}>
              <Text style={styles.seeAll}>View All</Text>
            </TouchableOpacity>
          </View>
          {Object.entries(dashboard.pendingByType)
            .filter(([, count]) => count > 0)
            .map(([type, count]) => (
              <ApprovalTypeRow
                key={type}
                type={type}
                count={count as number}
                onPress={() => go('Approvals', { filterType: type })}
              />
            ))}
        </View>
      )}

      {/* Quick actions */}
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Quick Actions</Text>
        <View style={styles.quickGrid}>
          <ManagerQuickAction
            icon="people-outline" label="My Team"
            onPress={() => go('Team')}
          />
          <ManagerQuickAction
            icon="calendar-outline" label="My Leave"
            onPress={() => go('ApplyLeave')}
          />
          <ManagerQuickAction
            icon="analytics-outline" label="Attendance"
            onPress={() => go('Team')}
          />
          <ManagerQuickAction
            icon="sparkles-outline" label="AI Insights"
            onPress={() => go('AIAssistant')}
          />
        </View>
      </View>

      {/* OT alert */}
      {dashboard?.overtimeAlert && dashboard.overtimeAlert.thisMonth > 0 && (
        <View style={styles.section}>
          <TouchableOpacity
            style={styles.otCard}
            onPress={() => go('Approvals', { filterType: 'OVERTIME' })}
          >
            <View>
              <Text style={styles.otCardLabel}>Overtime This Month</Text>
              <Text style={styles.otCardValue}>{dashboard.overtimeAlert.thisMonth}h</Text>
              <Text style={styles.otCardCompare}>
                vs {dashboard.overtimeAlert.lastMonth}h last month
              </Text>
            </View>
            <Ionicons name="time-outline" size={32} color={COLORS.warning} />
          </TouchableOpacity>
        </View>
      )}

      <View style={{ height: 32 }} />
    </ScrollView>
  );
}

function TeamStatCard({
  label, value, color, icon,
}: { label: string; value: number; color: string; icon: string }) {
  return (
    <View style={[styles.teamStatCard, { borderTopColor: color }]}>
      <Ionicons name={icon as any} size={18} color={color} />
      <Text style={[styles.teamStatValue, { color }]}>{value}</Text>
      <Text style={styles.teamStatLabel}>{label}</Text>
    </View>
  );
}

function ApprovalTypeRow({
  type, count, onPress,
}: { type: string; count: number; onPress: () => void }) {
  const typeLabels: Record<string, { label: string; icon: string; color: string }> = {
    LEAVE: { label: 'Leave Requests', icon: 'airplane-outline', color: COLORS.blue },
    OVERTIME: { label: 'Overtime Requests', icon: 'time-outline', color: '#7C3AED' },
    ATTENDANCE_CORRECTION: { label: 'Attendance Corrections', icon: 'create-outline', color: COLORS.warning },
    RECRUITMENT_REQUISITION: { label: 'Recruitment', icon: 'briefcase-outline', color: COLORS.success },
    HR_REQUEST: { label: 'HR Requests', icon: 'help-circle-outline', color: COLORS.cyan },
  };
  const meta = typeLabels[type] ?? { label: type, icon: 'document-outline', color: COLORS.muted };

  return (
    <TouchableOpacity style={styles.approvalTypeRow} onPress={onPress}>
      <View style={[styles.approvalTypeIcon, { backgroundColor: `${meta.color}15` }]}>
        <Ionicons name={meta.icon as any} size={18} color={meta.color} />
      </View>
      <Text style={styles.approvalTypeLabel}>{meta.label}</Text>
      <View style={[styles.approvalTypeBadge, { backgroundColor: `${meta.color}20` }]}>
        <Text style={[styles.approvalTypeBadgeText, { color: meta.color }]}>{count}</Text>
      </View>
      <Ionicons name="chevron-forward" size={14} color={COLORS.muted} />
    </TouchableOpacity>
  );
}

function ManagerQuickAction({
  icon, label, onPress,
}: { icon: string; label: string; onPress: () => void }) {
  return (
    <TouchableOpacity style={styles.managerQA} onPress={onPress} activeOpacity={0.7}>
      <Ionicons name={icon as any} size={22} color={COLORS.blue} />
      <Text style={styles.managerQALabel}>{label}</Text>
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
  headerRow: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 },
  headerActions: { flexDirection: 'row', gap: 8 },
  headerIcon: {
    width: 40, height: 40, borderRadius: 12,
    backgroundColor: 'rgba(255,255,255,0.1)',
    alignItems: 'center', justifyContent: 'center',
  },
  greeting: { fontSize: 13, color: 'rgba(255,255,255,0.5)' },
  name: { fontSize: 22, fontWeight: '700', color: '#fff' },
  dateText: { fontSize: 12, color: 'rgba(255,255,255,0.4)' },
  approvalBanner: { marginHorizontal: 16, marginTop: 16, borderRadius: 14, overflow: 'hidden' },
  approvalBannerInner: { flexDirection: 'row', alignItems: 'center', padding: 16, gap: 12 },
  approvalBannerTitle: { fontSize: 15, fontWeight: '700', color: '#fff' },
  approvalBannerSub: { fontSize: 12, color: 'rgba(255,255,255,0.7)', marginTop: 1 },
  section: { paddingHorizontal: 16, marginTop: 20 },
  sectionHeader: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10 },
  sectionTitle: { fontSize: 16, fontWeight: '700', color: COLORS.text, marginBottom: 12 },
  seeAll: { fontSize: 13, color: COLORS.blue, fontWeight: '600' },
  teamGrid: { flexDirection: 'row', gap: 10, marginBottom: 10 },
  teamStatCard: {
    flex: 1, backgroundColor: COLORS.card, borderRadius: 12,
    padding: 12, alignItems: 'center', gap: 4,
    borderTopWidth: 3,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.05, shadowRadius: 3, elevation: 2,
  },
  teamStatValue: { fontSize: 22, fontWeight: '800' },
  teamStatLabel: { fontSize: 11, color: COLORS.muted, fontWeight: '500' },
  lateAlert: {
    flexDirection: 'row', alignItems: 'center', gap: 8,
    backgroundColor: `${COLORS.warning}10`,
    borderRadius: 10, padding: 10,
    borderWidth: 1, borderColor: `${COLORS.warning}25`,
  },
  lateAlertText: { flex: 1, fontSize: 13, color: COLORS.text },
  lateAlertLink: { fontSize: 13, color: COLORS.warning, fontWeight: '600' },
  approvalTypeRow: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: COLORS.card, borderRadius: 12,
    padding: 14, marginBottom: 8, gap: 12,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.04, shadowRadius: 3, elevation: 1,
  },
  approvalTypeIcon: { width: 36, height: 36, borderRadius: 10, alignItems: 'center', justifyContent: 'center' },
  approvalTypeLabel: { flex: 1, fontSize: 14, fontWeight: '600', color: COLORS.text },
  approvalTypeBadge: { paddingHorizontal: 10, paddingVertical: 3, borderRadius: 20 },
  approvalTypeBadgeText: { fontSize: 13, fontWeight: '700' },
  quickGrid: { flexDirection: 'row', gap: 10 },
  managerQA: {
    flex: 1, backgroundColor: COLORS.card, borderRadius: 14, padding: 14,
    alignItems: 'center', gap: 8,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.05, shadowRadius: 3, elevation: 2,
  },
  managerQALabel: { fontSize: 12, color: COLORS.text, fontWeight: '600', textAlign: 'center' },
  otCard: {
    flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center',
    backgroundColor: `${COLORS.warning}10`, borderRadius: 14,
    padding: 16, borderWidth: 1, borderColor: `${COLORS.warning}25`,
  },
  otCardLabel: { fontSize: 12, color: COLORS.muted, marginBottom: 4 },
  otCardValue: { fontSize: 28, fontWeight: '800', color: COLORS.text },
  otCardCompare: { fontSize: 12, color: COLORS.muted, marginTop: 2 },
});
