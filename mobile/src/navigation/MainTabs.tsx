import React from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useAuthStore } from '@/auth/authStore';
import { isManagerUser } from './routes';
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
import ChangePasswordScreen from '@/features/auth/ChangePasswordScreen';

const Tab = createBottomTabNavigator();
const Stack = createNativeStackNavigator();
function MoreStack() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="MoreHome" component={MoreHomeScreen} />
      <Stack.Screen name="Profile" component={ProfileScreen} />
      <Stack.Screen name="Documents" component={DocumentsScreen} />
      <Stack.Screen name="HRRequests" component={HRRequestsScreen} />
      <Stack.Screen name="HRRequestDetail" component={HRRequestDetailScreen} />
      <Stack.Screen name="Notifications" component={NotificationsScreen} />
      <Stack.Screen name="AIAssistant" component={AIAssistantScreen} />
      <Stack.Screen name="Settings" component={SettingsScreen} />
      <Stack.Screen name="ChangePassword" component={ChangePasswordScreen} />
      <Stack.Screen name="AttendanceCorrection" component={AttendanceCorrectionScreen} />
      <Stack.Screen name="ApplyLeave" component={ApplyLeaveScreen} />
      <Stack.Screen name="Overtime" component={OvertimeScreen} />
      <Stack.Screen name="PayslipsList" component={PayslipsScreen} />
      <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />
    </Stack.Navigator>
  );
}

interface MoreItem {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  label: string;
  subtitle: string;
  screen: string;
  accent: string;
}
function MoreHomeScreen() {
  const { user } = useAuthStore();
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const manager = isManagerUser(user);

  const items: MoreItem[] = [
    ...(manager
      ? [
          {
            icon: 'calendar-outline' as const,
            label: 'Apply Leave',
            subtitle: 'Request time away',
            screen: 'ApplyLeave',
            accent: theme.colors.primary,
          },
          {
            icon: 'wallet-outline' as const,
            label: 'Payslips',
            subtitle: 'Salary & statements',
            screen: 'PayslipsList',
            accent: theme.colors.success,
          },
        ]
      : []),
    {
      icon: 'person-circle-outline',
      label: 'Profile',
      subtitle: 'Personal details',
      screen: 'Profile',
      accent: theme.colors.primary,
    },
    {
      icon: 'folder-open-outline',
      label: 'Documents',
      subtitle: 'Letters & records',
      screen: 'Documents',
      accent: theme.colors.violet,
    },
    {
      icon: 'chatbox-ellipses-outline',
      label: 'HR Requests',
      subtitle: 'Helpdesk & status',
      screen: 'HRRequests',
      accent: theme.colors.warning,
    },
    {
      icon: 'notifications-outline',
      label: 'Notifications',
      subtitle: 'Alerts & updates',
      screen: 'Notifications',
      accent: theme.colors.danger,
    },
    {
      icon: 'sparkles-outline',
      label: 'AI Assistant',
      subtitle: 'Ask workforce questions',
      screen: 'AIAssistant',
      accent: theme.colors.cyan,
    },
    {
      icon: 'time-outline',
      label: 'Overtime',
      subtitle: 'Submit & track',
      screen: 'Overtime',
      accent: theme.colors.violet,
    },
    {
      icon: 'settings-outline',
      label: 'Settings',
      subtitle: 'Security & preferences',
      screen: 'Settings',
      accent: theme.colors.textSecondary,
    },
  ];

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
      </ScrollView>
    </View>
  );
}
function EmployeeTabs() {
  return (
    <Tab.Navigator
      tabBar={(props) => <LiquidTabBar {...props} />}
      screenOptions={{
        headerShown: false,
        tabBarHideOnKeyboard: true,
      }}
    >
      <Tab.Screen name="Home" component={EmployeeHomeStack} />
      <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />
      <Tab.Screen name="Leave" component={ApplyLeaveScreen} />
      <Tab.Screen name="Payslips" component={PayslipsStack} />
      <Tab.Screen name="More" component={MoreStack} />
    </Tab.Navigator>
  );
}

function ManagerTabs() {
  return (
    <Tab.Navigator
      tabBar={(props) => <LiquidTabBar {...props} />}
      screenOptions={{
        headerShown: false,
        tabBarHideOnKeyboard: true,
      }}
    >
      <Tab.Screen name="Home" component={ManagerHomeStack} />
      <Tab.Screen name="Team" component={TeamScreen} />
      <Tab.Screen name="Approvals" component={ApprovalsScreen} />
      <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} />
      <Tab.Screen name="More" component={MoreStack} />
    </Tab.Navigator>
  );
}
function EmployeeHomeStack() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="Dashboard" component={EmployeeDashboard} />
      <Stack.Screen name="PayslipDetail" component={PayslipDetailScreen} />
      <Stack.Screen name="AttendanceCorrection" component={AttendanceCorrectionScreen} />
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

export function MainTabs() {
  const { user } = useAuthStore();
  return isManagerUser(user) ? <ManagerTabs /> : <EmployeeTabs />;
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
});
