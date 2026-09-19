import React from 'react';
import { View, Text, TouchableOpacity } from 'react-native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';
import { isManagerUser } from './routes';

// Screens
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

// ─── Tab bar icon ────────────────────────────────────────────────────────────
function TabIcon({ emoji, label, focused }: { emoji: string; label: string; focused: boolean }) {
  return (
    <View style={{ alignItems: 'center', paddingTop: 4 }}>
      <Text style={{ fontSize: 20 }}>{emoji}</Text>
      <Text style={{
        fontSize: 10, marginTop: 1,
        color: focused ? COLORS.blue : '#9CA3AF',
        fontWeight: focused ? '700' : '400',
      }}>
        {label}
      </Text>
    </View>
  );
}

// ─── More stack (shared) ─────────────────────────────────────────────────────
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

// ─── More home screen ─────────────────────────────────────────────────────────
function MoreHomeScreen() {
  const { user } = useAuthStore();
  const navigation = useMoreNavigation();
  const manager = isManagerUser(user);

  // Managers' tab bar has Team/Approvals instead of Leave/Payslips, so their
  // own leave and payslips are reachable from here.
  const items = [
    ...(manager
      ? [
          { icon: '🌴', label: 'Apply Leave', screen: 'ApplyLeave' },
          { icon: '💰', label: 'Payslips', screen: 'PayslipsList' },
        ]
      : []),
    { icon: '👤', label: 'Profile', screen: 'Profile' },
    { icon: '📄', label: 'Documents', screen: 'Documents' },
    { icon: '🎫', label: 'HR Requests', screen: 'HRRequests' },
    { icon: '🔔', label: 'Notifications', screen: 'Notifications' },
    { icon: '🤖', label: 'AI Assistant', screen: 'AIAssistant' },
    { icon: '⏰', label: 'Overtime', screen: 'Overtime' },
    { icon: '⚙️', label: 'Settings', screen: 'Settings' },
  ];

  return (
    <View style={{ flex: 1, backgroundColor: '#F8FAFC' }}>
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>More</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          {user?.name} · {user?.role}
        </Text>
      </View>
      <View style={{ padding: 16 }}>
        <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 12 }}>
          {items.map((item) => (
            <TouchableOpacity
              key={item.screen}
              onPress={() => navigation.navigate(item.screen)}
              style={{
                width: '30%', backgroundColor: '#fff', borderRadius: 14, padding: 16,
                alignItems: 'center',
                shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
              }}
            >
              <Text style={{ fontSize: 28 }}>{item.icon}</Text>
              <Text style={{ fontSize: 12, color: '#374151', fontWeight: '500', marginTop: 6, textAlign: 'center' }}>
                {item.label}
              </Text>
            </TouchableOpacity>
          ))}
        </View>
      </View>
    </View>
  );
}

// Hook to get navigation from More screens
function useMoreNavigation() {
  return useNavigation<any>();
}

// ─── Employee tabs ────────────────────────────────────────────────────────────
function EmployeeTabs() {
  return (
    <Tab.Navigator
      screenOptions={{
        headerShown: false,
        tabBarStyle: {
          backgroundColor: '#fff',
          borderTopWidth: 1,
          borderTopColor: '#F3F4F6',
          height: 72,
          paddingBottom: 8,
        },
        tabBarShowLabel: false,
      }}
    >
      <Tab.Screen
        name="Home"
        component={EmployeeHomeStack}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🏠" label="Home" focused={focused} /> }}
      />
      <Tab.Screen
        name="Attendance"
        component={AttendanceHistoryScreen}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📍" label="Attendance" focused={focused} /> }}
      />
      <Tab.Screen
        name="Leave"
        component={ApplyLeaveScreen}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🌴" label="Leave" focused={focused} /> }}
      />
      <Tab.Screen
        name="Payslips"
        component={PayslipsStack}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="💰" label="Payslips" focused={focused} /> }}
      />
      <Tab.Screen
        name="More"
        component={MoreStack}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="⋯" label="More" focused={focused} /> }}
      />
    </Tab.Navigator>
  );
}

// ─── Manager/Supervisor tabs ──────────────────────────────────────────────────
function ManagerTabs() {
  return (
    <Tab.Navigator
      screenOptions={{
        headerShown: false,
        tabBarStyle: {
          backgroundColor: '#fff',
          borderTopWidth: 1,
          borderTopColor: '#F3F4F6',
          height: 72,
          paddingBottom: 8,
        },
        tabBarShowLabel: false,
      }}
    >
      <Tab.Screen
        name="Home"
        component={ManagerHomeStack}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="🏠" label="Home" focused={focused} /> }}
      />
      <Tab.Screen
        name="Team"
        component={TeamScreen}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="👥" label="Team" focused={focused} /> }}
      />
      <Tab.Screen
        name="Approvals"
        component={ApprovalsScreen}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="✅" label="Approvals" focused={focused} /> }}
      />
      <Tab.Screen
        name="Attendance"
        component={AttendanceHistoryScreen}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="📍" label="Attendance" focused={focused} /> }}
      />
      <Tab.Screen
        name="More"
        component={MoreStack}
        options={{ tabBarIcon: ({ focused }) => <TabIcon emoji="⋯" label="More" focused={focused} /> }}
      />
    </Tab.Navigator>
  );
}

// Sub-stacks for Home screens (needed to navigate to PayslipDetail etc.)
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

// ─── Main export ──────────────────────────────────────────────────────────────
export function MainTabs() {
  const { user } = useAuthStore();
  // user.role is the normalised upper-case role ('MANAGER', 'HR', …) from mapRole();
  // the old check compared it to 'Manager' and so never showed the manager tabs.
  return isManagerUser(user) ? <ManagerTabs /> : <EmployeeTabs />;
}
