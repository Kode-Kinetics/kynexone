import React, { useMemo, useState } from 'react';
import { ActivityIndicator, Alert, ScrollView, StyleSheet, Text, View, type ViewStyle } from 'react-native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { deriveMobileAccess, type MobileSurface } from '@/auth/accessPolicy';
import { LiquidTabBar } from './LiquidTabBar';
import { useTabBarInset } from './tabBarInset';
import {
  DirectionalIcon,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  Surface,
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
import SelfieConsentScreen from '@/features/attendance/SelfieConsentScreen';
import SessionScreen from '@/features/settings/SessionScreen';
import ChangePasswordScreen from '@/features/auth/ChangePasswordScreen';

const Tab = createBottomTabNavigator();
const Stack = createNativeStackNavigator();

const tabScreenOptions = {
  headerShown: false,
  tabBarHideOnKeyboard: true,
} as const;

const renderTabBar = (props: React.ComponentProps<typeof LiquidTabBar>) => <LiquidTabBar {...props} />;

/**
 * The tab bar floats over the bottom of the window. Every tab scene stops above it (so no screen's
 * last row can hide behind it), and the scene background is transparent so the one static backdrop
 * rendered by TabShell shows through around the capsule — seamless with each screen's own backdrop.
 */
function useSceneContainerStyle(): ViewStyle {
  const inset = useTabBarInset();
  return useMemo(() => ({ backgroundColor: 'transparent', paddingBottom: inset }), [inset]);
}

function TabShell({ children }: { children: React.ReactNode }) {
  const { theme } = useTheme();
  return (
    <View style={[styles.shell, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop />
      {children}
    </View>
  );
}

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
      {surfaces.has('settings') && <Stack.Screen name="SelfieConsent" component={SelfieConsentScreen} />}
      {surfaces.has('attendanceCorrection') && <Stack.Screen name="AttendanceCorrection" component={AttendanceCorrectionScreen} />}
      {surfaces.has('leave') && <Stack.Screen name="ApplyLeave" component={ApplyLeaveScreen} />}
      {surfaces.has('overtime') && <Stack.Screen name="Overtime" component={OvertimeScreen} />}
      {surfaces.has('payslips') && <Stack.Screen name="PayslipsList" component={PayslipsScreen} />}
      {surfaces.has('payslips') && <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />}
      <Stack.Screen name="Account" component={SessionScreen} />
    </Stack.Navigator>
  );
}

type MoreItemKey =
  | 'applyLeave' | 'payslips' | 'profile' | 'documents' | 'hrRequests'
  | 'notifications' | 'aiAssistant' | 'overtime' | 'settings' | 'account';

interface MoreItem {
  surface: MobileSurface;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  key: MoreItemKey;
  screen: string;
}

// Every tile is gated by the same access policy that registers its route.
const MORE_ITEMS: MoreItem[] = [
  { surface: 'leave', icon: 'calendar-outline', key: 'applyLeave', screen: 'ApplyLeave' },
  { surface: 'payslips', icon: 'wallet-outline', key: 'payslips', screen: 'PayslipsList' },
  { surface: 'profile', icon: 'person-circle-outline', key: 'profile', screen: 'Profile' },
  { surface: 'documents', icon: 'folder-open-outline', key: 'documents', screen: 'Documents' },
  { surface: 'hrRequests', icon: 'chatbox-ellipses-outline', key: 'hrRequests', screen: 'HRRequests' },
  { surface: 'notifications', icon: 'notifications-outline', key: 'notifications', screen: 'Notifications' },
  { surface: 'aiAssistant', icon: 'sparkles-outline', key: 'aiAssistant', screen: 'AIAssistant' },
  { surface: 'overtime', icon: 'time-outline', key: 'overtime', screen: 'Overtime' },
  { surface: 'settings', icon: 'settings-outline', key: 'settings', screen: 'Settings' },
  { surface: 'account', icon: 'id-card-outline', key: 'account', screen: 'Account' },
];

function MoreHomeScreen() {
  const { user, logout } = useAuthStore();
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const [loggingOut, setLoggingOut] = useState(false);
  const surfaces = deriveMobileAccess(user).surfaces;
  const items = MORE_ITEMS.filter((item) => surfaces.has(item.surface));
  const danger = theme.colors.status.danger;

  const displayName = (i18n.language?.startsWith('ar') ? user?.fullNameAr : undefined)
    || user?.name || user?.fullName || t('more.fallbackName');
  const subtitle = [displayName, user?.jobTitle].filter(Boolean).join(' · ');

  const confirmLogout = () => {
    Alert.alert(t('more.signOutConfirmTitle'), t('more.signOutConfirmBody'), [
      { text: t('common.cancel'), style: 'cancel' },
      {
        text: t('more.signOut'),
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

  return (
    <View style={[styles.moreRoot, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.moreContent}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero eyebrow={t('more.eyebrow')} title={t('more.title')} subtitle={subtitle} />

        <View style={styles.moreGrid}>
          {items.map((item) => {
            const label = t(`more.items.${item.key}`);
            const description = t(`more.items.${item.key}Sub`);
            return (
              <MotionPressable
                key={item.screen}
                accessibilityRole="button"
                accessibilityLabel={`${label}. ${description}`}
                onPress={() => navigation.navigate(item.screen)}
                style={styles.moreTileShell}
                contentStyle={styles.moreTilePressable}
              >
                <Surface radius={theme.radius.lg} contentStyle={styles.moreTile} style={styles.moreTileSurface}>
                  <View style={styles.moreTileTop}>
                    <View style={[styles.moreIcon, { backgroundColor: `${theme.colors.accent}14` }]}>
                      <Ionicons accessible={false} name={item.icon} size={22} color={theme.colors.accent} />
                    </View>
                    <DirectionalIcon name="chevron" size={18} color={theme.colors.textMuted} />
                  </View>
                  <View style={styles.moreTileCopy}>
                    <Text
                      numberOfLines={2}
                      style={[theme.typography.headline, { color: theme.colors.text }]}
                    >
                      {label}
                    </Text>
                    <Text
                      numberOfLines={2}
                      style={[theme.typography.footnote, { color: theme.colors.textSecondary, marginTop: 2 }]}
                    >
                      {description}
                    </Text>
                  </View>
                </Surface>
              </MotionPressable>
            );
          })}
        </View>

        <View style={styles.sessionSection}>
          <Text
            accessibilityRole="header"
            style={[theme.typography.eyebrow, styles.sessionLabel, { color: theme.colors.textMuted }]}
          >
            {t('more.session')}
          </Text>
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={t('more.signOutA11y')}
            accessibilityState={{ busy: loggingOut, disabled: loggingOut }}
            onPress={confirmLogout}
            disabled={loggingOut}
            haptic="commit"
            contentStyle={styles.signOutPressable}
          >
            <Surface radius={theme.radius.lg} contentStyle={styles.signOutCard}>
              <View style={[styles.signOutIcon, { backgroundColor: danger.tint }]}>
                <Ionicons accessible={false} name="log-out-outline" size={22} color={danger.fg} />
              </View>
              <View style={styles.signOutCopy}>
                <Text style={[theme.typography.headline, { color: danger.fg }]}>
                  {loggingOut ? t('more.signingOut') : t('more.signOut')}
                </Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary, marginTop: 2 }]}>
                  {t('more.signOutBody')}
                </Text>
              </View>
              {loggingOut ? (
                <ActivityIndicator color={danger.fg} size="small" />
              ) : (
                <DirectionalIcon name="chevron" size={18} color={theme.colors.textMuted} />
              )}
            </Surface>
          </MotionPressable>
        </View>
      </ScrollView>
    </View>
  );
}

function EmployeeTabs() {
  const sceneContainerStyle = useSceneContainerStyle();
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <TabShell>
      <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions} sceneContainerStyle={sceneContainerStyle}>
        {s.has('employeeHome') && <Tab.Screen name="Home" component={EmployeeHomeStack} />}
        {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
        {s.has('leave') && <Tab.Screen name="Leave" component={ApplyLeaveScreen} />}
        {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} />}
        <Tab.Screen name="More" component={MoreStack} />
      </Tab.Navigator>
    </TabShell>
  );
}

function ManagerTabs() {
  const sceneContainerStyle = useSceneContainerStyle();
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <TabShell>
      <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions} sceneContainerStyle={sceneContainerStyle}>
        {s.has('managerHome') && <Tab.Screen name="Home" component={ManagerHomeStack} />}
        {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} />}
        {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
        {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
        <Tab.Screen name="More" component={MoreStack} />
      </Tab.Navigator>
    </TabShell>
  );
}

function SpecialistTabs() {
  const sceneContainerStyle = useSceneContainerStyle();
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  const initialRouteName = s.has('approvals') ? 'Approvals' : s.has('payslips') ? 'Payslips' : s.has('attendance') ? 'Attendance' : 'More';
  return (
    <TabShell>
      <Tab.Navigator initialRouteName={initialRouteName} tabBar={renderTabBar} screenOptions={tabScreenOptions} sceneContainerStyle={sceneContainerStyle}>
        {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
        {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} />}
        {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
        <Tab.Screen name="More" component={MoreStack} />
      </Tab.Navigator>
    </TabShell>
  );
}

function KioskTabs() {
  const sceneContainerStyle = useSceneContainerStyle();
  return (
    <TabShell>
      <Tab.Navigator initialRouteName="Punch" tabBar={renderTabBar} screenOptions={tabScreenOptions} sceneContainerStyle={sceneContainerStyle}>
        <Tab.Screen name="Punch" component={KioskAttendanceScreen} />
        <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />
        <Tab.Screen name="Account" component={SessionScreen} />
      </Tab.Navigator>
    </TabShell>
  );
}

function LimitedAccessTabs() {
  const sceneContainerStyle = useSceneContainerStyle();
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <TabShell>
      <Tab.Navigator initialRouteName={policy.landing} tabBar={renderTabBar} screenOptions={tabScreenOptions} sceneContainerStyle={sceneContainerStyle}>
        {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} />}
        {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} />}
        {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />}
        <Tab.Screen name="Account" component={SessionScreen} />
      </Tab.Navigator>
    </TabShell>
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
  shell: { flex: 1 },
  moreRoot: { flex: 1 },
  moreContent: { paddingBottom: 24 },
  moreGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    paddingHorizontal: 16,
    paddingTop: 4,
    gap: 12,
  },
  moreTileShell: {
    width: '48%',
    minHeight: 138,
  },
  moreTilePressable: {
    flex: 1,
  },
  moreTileSurface: {
    flex: 1,
  },
  moreTile: {
    flex: 1,
    padding: 14,
    justifyContent: 'space-between',
  },
  moreTileTop: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
  },
  moreIcon: {
    width: 40,
    height: 40,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
  },
  moreTileCopy: {
    marginTop: 14,
  },
  sessionSection: {
    paddingHorizontal: 16,
    paddingTop: 24,
  },
  sessionLabel: {
    marginBottom: 8,
    marginStart: 4,
  },
  signOutPressable: {
    borderRadius: 22,
  },
  signOutCard: {
    minHeight: 72,
    paddingHorizontal: 16,
    paddingVertical: 12,
    flexDirection: 'row',
    alignItems: 'center',
  },
  signOutIcon: {
    width: 40,
    height: 40,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
  },
  signOutCopy: {
    flex: 1,
    marginHorizontal: 12,
  },
});
