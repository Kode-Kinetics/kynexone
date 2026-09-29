import React from 'react';
import { Text, TouchableOpacity, View } from 'react-native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { useAuthStore } from '@/auth/authStore';
import { deriveMobileAccess, type MobileSurface } from '@/auth/accessPolicy';
import { COLORS } from '@/config';

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

function TabIcon({ emoji, label, focused }: { emoji: string; label: string; focused: boolean }) {
  return (
    <View style={{ alignItems: 'center', paddingTop: 4 }}>
      <Text style={{ fontSize: 20 }}>{emoji}</Text>
      <Text style={{ fontSize: 10, marginTop: 1, color: focused ? COLORS.blue : '#9CA3AF', fontWeight: focused ? '700' : '400' }}>
        {label}
      </Text>
    </View>
  );
}

const tabScreenOptions = {
  headerShown: false,
  tabBarStyle: { backgroundColor: '#fff', borderTopWidth: 1, borderTopColor: '#F3F4F6', height: 72, paddingBottom: 8 },
  tabBarShowLabel: false,
} as const;

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

function MoreHomeScreen() {
  const user = useAuthStore((state) => state.user);
  const navigation = useNavigation<any>();
  const surfaces = deriveMobileAccess(user).surfaces;
  const candidates: { surface: MobileSurface; icon: string; label: string; screen: string }[] = [
    { surface: 'leave', icon: '🌴', label: 'Apply Leave', screen: 'ApplyLeave' },
    { surface: 'payslips', icon: '💰', label: 'Payslips', screen: 'PayslipsList' },
    { surface: 'profile', icon: '👤', label: 'Profile', screen: 'Profile' },
    { surface: 'documents', icon: '📄', label: 'Documents', screen: 'Documents' },
    { surface: 'hrRequests', icon: '🎫', label: 'HR Requests', screen: 'HRRequests' },
    { surface: 'notifications', icon: '🔔', label: 'Notifications', screen: 'Notifications' },
    { surface: 'aiAssistant', icon: '🤖', label: 'AI Assistant', screen: 'AIAssistant' },
    { surface: 'overtime', icon: '⏰', label: 'Overtime', screen: 'Overtime' },
    { surface: 'settings', icon: '⚙️', label: 'Settings', screen: 'Settings' },
    { surface: 'account', icon: '🚪', label: 'Account', screen: 'Account' },
  ];
  const items = candidates.filter((item) => surfaces.has(item.surface));

  return (
    <View style={{ flex: 1, backgroundColor: '#F8FAFC' }}>
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>More</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>{user?.name} · {user?.role}</Text>
      </View>
      <View style={{ padding: 16, flexDirection: 'row', flexWrap: 'wrap', gap: 12 }}>
        {items.map((item) => (
          <TouchableOpacity
            key={item.screen}
            onPress={() => navigation.navigate(item.screen)}
            style={{ width: '30%', backgroundColor: '#fff', borderRadius: 14, padding: 16, alignItems: 'center', elevation: 2 }}
          >
            <Text style={{ fontSize: 28 }}>{item.icon}</Text>
            <Text style={{ fontSize: 12, color: '#374151', fontWeight: '500', marginTop: 6, textAlign: 'center' }}>{item.label}</Text>
          </TouchableOpacity>
        ))}
      </View>
    </View>
  );
}

function EmployeeTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} screenOptions={tabScreenOptions}>
      {s.has('employeeHome') && <Tab.Screen name="Home" component={EmployeeHomeStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🏠" label="Home" focused={focused} /> }} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📍" label="Attendance" focused={focused} /> }} />}
      {s.has('leave') && <Tab.Screen name="Leave" component={ApplyLeaveScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🌴" label="Leave" focused={focused} /> }} />}
      {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="💰" label="Payslips" focused={focused} /> }} />}
      <Tab.Screen name="More" component={MoreStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="⋯" label="More" focused={focused} /> }} />
    </Tab.Navigator>
  );
}

function ManagerTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} screenOptions={tabScreenOptions}>
      {s.has('managerHome') && <Tab.Screen name="Home" component={ManagerHomeStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🏠" label="Home" focused={focused} /> }} />}
      {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👥" label="Team" focused={focused} /> }} />}
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="✅" label="Approvals" focused={focused} /> }} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📍" label="Attendance" focused={focused} /> }} />}
      <Tab.Screen name="More" component={MoreStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="⋯" label="More" focused={focused} /> }} />
    </Tab.Navigator>
  );
}

function SpecialistTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  const initialRouteName = s.has('approvals') ? 'Approvals' : s.has('payslips') ? 'Payslips' : s.has('attendance') ? 'Attendance' : 'More';
  return (
    <Tab.Navigator initialRouteName={initialRouteName} screenOptions={tabScreenOptions}>
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="✅" label="Approvals" focused={focused} /> }} />}
      {s.has('payslips') && <Tab.Screen name="Payslips" component={PayslipsStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="💰" label="Payslips" focused={focused} /> }} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📍" label="Attendance" focused={focused} /> }} />}
      <Tab.Screen name="More" component={MoreStack} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="⋯" label="More" focused={focused} /> }} />
    </Tab.Navigator>
  );
}

function KioskTabs() {
  return (
    <Tab.Navigator initialRouteName="Punch" screenOptions={tabScreenOptions}>
      <Tab.Screen name="Punch" component={KioskAttendanceScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🟢" label="Punch" focused={focused} /> }} />
      <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📅" label="History" focused={focused} /> }} />
      <Tab.Screen name="Account" component={SessionScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👤" label="Account" focused={focused} /> }} />
    </Tab.Navigator>
  );
}

function LimitedAccessTabs() {
  const user = useAuthStore((state) => state.user);
  const policy = deriveMobileAccess(user);
  const s = policy.surfaces;
  return (
    <Tab.Navigator initialRouteName={policy.landing} screenOptions={tabScreenOptions}>
      {s.has('team') && <Tab.Screen name="Team" component={TeamScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👥" label="Team" focused={focused} /> }} />}
      {s.has('approvals') && <Tab.Screen name="Approvals" component={ApprovalsScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👁" label="Approvals" focused={focused} /> }} />}
      {s.has('attendance') && <Tab.Screen name="Attendance" component={AttendanceHistoryScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📅" label="Attendance" focused={focused} /> }} />}
      <Tab.Screen name="Account" component={SessionScreen} options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👤" label="Account" focused={focused} /> }} />
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
