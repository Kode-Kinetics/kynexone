import React, { useState } from 'react';
import { ActivityIndicator, Alert, ScrollView, StyleSheet, Text, View } from 'react-native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useAuthStore } from '@/auth/authStore';
import { deriveMobileAccess, type MobileSurface } from '@/auth/accessPolicy';
import { LiquidTabBar } from './LiquidTabBar';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

import EmployeeDashboard from '@/features/dashboard/EmployeeDashboard';
import ManagerDashboard from '@/features/dashboard/ManagerDashboard';
import TeamScreen from '@/features/dashboard/TeamScreen';
import AttendanceHistoryScreen from '@/features/attendance/AttendanceHistoryScreen';
import AttendanceCorrectionScreen from '@/features/attendance/AttendanceCorrectionScreen';
import KioskAttendanceScreen from '@/features/attendance/KioskAttendanceScreen';
import ApplyLeaveScreen from '@/features/leave/ApplyLeaveScreen';
import OvertimeScreen from '@/features/overtime/OvertimeScreen';
import ApprovalsScreen from '@/features/approvals/ApprovalsScreen';
import PayslipsScreen from '@/features/payslips/PayslipsScreen';
import PayslipDetailScreen from '@/features/payslips/PayslipDetailScreen';
import ProfileScreen from '@/features/profile/ProfileScreen';
import DocumentsScreen from '@/features/documents/DocumentsScreen';
import HRRequestsScreen from '@/features/requests/HRRequestsScreen';
import HRRequestDetailScreen from '@/features/requests/HRRequestDetailScreen';
import NotificationsScreen from '@/features/notifications/NotificationsScreen';
import AIAssistantScreen from '@/features/ai-assistant/AIAssistantScreen';
import SettingsScreen from '@/features/settings/SettingsScreen';
import SessionScreen from '@/features/settings/SessionScreen';
import ChangePasswordScreen from '@/features/auth/ChangePasswordScreen';

const Tab = createBottomTabNavigator();
const Stack = createNativeStackNavigator();

const tabScreenOptions = {
  headerShown: false,
  tabBarHideOnKeyboard: true,
} as const;

const renderTabBar = (props: React.ComponentProps<typeof LiquidTabBar>) => <LiquidTabBar {...props} />;

function useSurface(surface: MobileSurface): boolean {
  const user = useAuthStore((state) => state.user);
  return deriveMobileAccess(user).surfaces.has(surface);
}

function EmployeeHomeStack() {
  const correction = useSurface('attendanceCorrection');
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="Dashboard" component={EmployeeDashboard} />
      <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />
      {correction && <Stack.Screen name="AttendanceCorrection" component={AttendanceCorrectionScreen} />}
    </Stack.Navigator>
  );
}

function ManagerHomeStack() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="Dashboard" component={ManagerDashboard} />
    </Stack.Navigator>
  );
}

function PayslipsStack() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="PayslipsList" component={PayslipsScreen} />
      <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />
    </Stack.Navigator>
  );
}

function MoreStack() {
  const user = useAuthStore((state) => state.user);
  const surfaces = deriveMobileAccess(user).surfaces;
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="MoreHome" component={MoreHomeScreen} />
      {surfaces.has('profile') && <Stack.Screen name="Profile" component={ProfileScreen} />}
      {surfaces.has('documents') && <Stack.Screen name="Documents" component={DocumentsScreen} />}
      {surfaces.has('hrRequests') && <Stack.Screen name="HRRequests" component={HRRequestsScreen} />}
      {surfaces.has('hrRequests') && <Stack.Screen name="HRRequestDetail" component={HRRequestDetailScreen} />}
      {surfaces.has('notifications') && <Stack.Screen name="Notifications" component={NotificationsScreen} />}
      {surfaces.has('aiAssistant') && <Stack.Screen name="AIAssistant" component={AIAssistantScreen} />}
      {surfaces.has('settings') && <Stack.Screen name="Settings" component={SettingsScreen} />}
      {surfaces.has('settings') && <Stack.Screen name="ChangePassword" component={ChangePasswordScreen} />}
      {surfaces.has('attendanceCorrection') && <Stack.Screen name="AttendanceCorrection" component={AttendanceCorrectionScreen} />}
      {surfaces.has('leave') && <Stack.Screen name="ApplyLeave" component={ApplyLeaveScreen} />}
      {surfaces.has('overtime') && <Stack.Screen name="Overtime" component={OvertimeScreen} />}
      {surfaces.has('payslips') && <Stack.Screen name="PayslipsList" component={PayslipsScreen} />}
      {surfaces.has('payslips') && <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />}
      <Stack.Screen name="Account" component={SessionScreen} />
    </Stack.Navigator>
  );
}

interface MoreItem {
  surface: MobileSurface;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  label: string;
  subtitle: string;
  screen: string;
  accent: string;
}

function MoreHomeScreen() {
  const { user, logout } = useAuthStore();
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const [loggingOut, setLoggingOut] = useState(false);
  const surfaces = deriveMobileAccess(user).surfaces;

  const confirmLogout = () => {
    Alert.alert('Sign out', 'End this secure session on this device?', [
      { text: 'Cancel', style: 'cancel' },
      {
        text: 'Sign out',
        style: 'destructive',
        onPress: async () => {
          setLoggingOut(true);
          try {
            await logout();
          } finally {
            setLoggingOut(false);
          }
        },
      },
    ]);
  };

  // Every tile is gated by the same access policy that registers its route.
  const candidates: MoreItem[] = [
    { surface: 'leave', icon: 'calendar-outline', label: 'Apply Leave', subtitle: 'Request time away', screen: 'ApplyLeave', accent: theme.colors.primary },
    { surface: 'payslips', icon: 'wallet-outline', label: 'Payslips', subtitle: 'Salary & statements', screen: 'PayslipsList', accent: theme.colors.success },
    { surface: 'profile', icon: 'person-circle-outline', label: 'Profile', subtitle: 'Personal details', screen: 'Profile', accent: theme.colors.primary },
    { surface: 'documents', icon: 'folder-open-outline', label: 'Documents', subtitle: 'Letters & records', screen: 'Documents', accent: theme.colors.violet },
    { surface: 'hrRequests', icon: 'chatbox-ellipses-outline', label: 'HR Requests', subtitle: 'Helpdesk & status', screen: 'HRRequests', accent: theme.colors.warning },
    { surface: 'notifications', icon: 'notifications-outline', label: 'Notifications', subtitle: 'Alerts & updates', screen: 'Notifications', accent: theme.colors.danger },
    { surface: 'aiAssistant', icon: 'sparkles-outline', label: 'AI Assistant', subtitle: 'Ask workforce questions', screen: 'AIAssistant', accent: theme.colors.cyan },
    { surface: 'overtime', icon: 'time-outline', label: 'Overtime', subtitle: 'Submit & track', screen: 'Overtime', accent: theme.colors.violet },
    { surface: 'settings', icon: 'settings-outline', label: 'Settings', subtitle: 'Security & preferences', screen: 'Settings', accent: theme.colors.textSecondary },
    { surface: 'account', icon: 'id-card-outline', label: 'Account', subtitle: 'Session & access', screen: 'Account', accent: theme.colors.textSecondary },
  ];
  const items = candidates.filter((item) => surfaces.has(item.surface));

  return (
    <View style={[styles.moreRoot, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.moreContent}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Workspace"
          title="More"
          subtitle={`${user?.name ?? 'Employee'} · ${user?.role ?? 'Workforce'}`}
        />

        <View style={styles.moreGrid}>
          {items.map((item) => (
            <MotionPressable
              key={item.screen}
              accessibilityRole="button"
              accessibilityLabel={`${item.label}. ${item.subtitle}`}
              onPress={() => navigation.navigate(item.screen)}
              haptic="selection"
              style={styles.moreTileShell}
              contentStyle={styles.moreTilePressable}
            >
              <GlassSurface
                elevated={false}
                radius={theme.radius.xl}
                contentStyle={styles.moreTile}
                style={styles.moreTileSurface}
              >
                <View
                  style={[
                    styles.moreIcon,
                    { backgroundColor: `${item.accent}18` },
                  ]}
                >
                  <Ionicons name={item.icon} size={24} color={item.accent} />
                </View>
                <View style={styles.moreTileCopy}>
                  <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                    {item.label}
                  </Text>
                  <Text
                    numberOfLines={2}
                    style={[
                      theme.typography.caption,
                      { color: theme.colors.textMuted, marginTop: 3 },
                    ]}
                  >
                    {item.subtitle}
                  </Text>
                </View>
                <Ionicons name="arrow-forward-circle-outline" size={20} color={theme.colors.textMuted} />
              </GlassSurface>
            </MotionPressable>
          ))}
        </View>

        <View style={styles.sessionSection}>
          <Text style={[theme.typography.micro, styles.sessionLabel, { color: theme.colors.textMuted }]}>SESSION</Text>
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel="Sign out of KynexOne"
            accessibilityState={{ busy: loggingOut, disabled: loggingOut }}
            onPress={confirmLogout}
            disabled={loggingOut}
            haptic="medium"
            contentStyle={styles.signOutPressable}
          >
            <GlassSurface
              elevated={false}
              radius={theme.radius.xl}
              tintColor={`${theme.colors.danger}0D`}
              contentStyle={styles.signOutCard}
            >
              <View style={[styles.signOutIcon, { backgroundColor: `${theme.colors.danger}18` }]}>
                <Ionicons name="log-out-outline" size={22} color={theme.colors.danger} />
              </View>
              <View style={styles.signOutCopy}>
                <Text style={[theme.typography.bodyStrong, { color: theme.colors.danger }]}>
                  {loggingOut ? 'Signing out…' : 'Sign out'}
                </Text>
                <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
                  Remove the secure session from this device
                </Text>
              </View>
              {loggingOut ? (
                <ActivityIndicator color={theme.colors.danger} size="small" />
              ) : (
                <Ionicons name="chevron-forward" size={20} color={theme.colors.danger} />
              )}
            </GlassSurface>
          </MotionPressable>
        </View>
      </ScrollView>
    </View>
  );
}

function EmployeeTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions}>
      {s.has('employeeHome') && <Tab.Screen name="Home" component={EmployeeHomeStack} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
      {s.has('leave') && <Tab.Screen name="Leave" component={ApplyLeaveScreen} />}
      {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} />}
      <Tab.Screen name="More" component={MoreStack} />
    </Tab.Navigator>
  );
}

function ManagerTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions}>
      {s.has('managerHome') && <Tab.Screen name="Home" component={ManagerHomeStack} />}
      {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} />}
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
      <Tab.Screen name="More" component={MoreStack} />
    </Tab.Navigator>
  );
}

function SpecialistTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  const initialRouteName = s.has('approvals') ? 'Approvals' : s.has('payslips') ? 'Payslips' : s.has('attendance') ? 'Attendance' : 'More';
  return (
    <Tab.Navigator initialRouteName={initialRouteName} tabBar={renderTabBar} screenOptions={tabScreenOptions}>
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
      {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
      <Tab.Screen name="More" component={MoreStack} />
    </Tab.Navigator>
  );
}

function KioskTabs() {
  return (
    <Tab.Navigator initialRouteName="Punch" tabBar={renderTabBar} screenOptions={tabScreenOptions}>
      <Tab.Screen name="Punch" component={KioskAttendanceScreen} />
      <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />
      <Tab.Screen name="Account" component={SessionScreen} />
    </Tab.Navigator>
  );
}

function LimitedAccessTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions}>
      {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} />}
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
      <Tab.Screen name="Account" component={SessionScreen} />
    </Tab.Navigator>
  );
}

export function MainTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  if (policy.mode === 'KioskOnly' && policy.surfaces.has('kioskPunch')) return <KioskTabs />;
  if (policy.mode === 'PayrollPortal' || policy.mode === 'FinancePortal'
    || user?.role === 'PAYROLL' || user?.role === 'FINANCE_APPROVER') return <SpecialistTabs />;
  if (policy.surfaces.has('managerHome')) return <ManagerTabs />;
  if (policy.surfaces.has('employeeHome')) return <EmployeeTabs />;
  if (policy.surfaces.size > 1) return <LimitedAccessTabs />;
  return <SessionScreen />;
}

const styles = StyleSheet.create({
  moreRoot: { flex: 1 },
  moreContent: { paddingBottom: 30 },
  moreGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    paddingHorizontal: 16,
    paddingTop: 12,
    gap: 12,
  },
  moreTileShell: {
    width: '48%',
    minHeight: 156,
  },
  moreTilePressable: {
    flex: 1,
    borderRadius: 24,
  },
  moreTileSurface: {
    flex: 1,
  },
  moreTile: {
    flex: 1,
    padding: 16,
    justifyContent: 'space-between',
  },
  moreIcon: {
    width: 48,
    height: 48,
    borderRadius: 17,
    alignItems: 'center',
    justifyContent: 'center',
  },
  moreTileCopy: {
    marginTop: 15,
    marginBottom: 10,
  },
  sessionSection: {
    paddingHorizontal: 16,
    paddingTop: 26,
  },
  sessionLabel: {
    fontWeight: '800',
    letterSpacing: 1.1,
    marginBottom: 9,
    marginLeft: 4,
  },
  signOutPressable: {
    borderRadius: 24,
  },
  signOutCard: {
    minHeight: 82,
    paddingHorizontal: 16,
    paddingVertical: 14,
    flexDirection: 'row',
    alignItems: 'center',
  },
  signOutIcon: {
    width: 46,
    height: 46,
    borderRadius: 16,
    alignItems: 'center',
    justifyContent: 'center',
  },
  signOutCopy: {
    flex: 1,
    marginHorizontal: 13,
  },
});
