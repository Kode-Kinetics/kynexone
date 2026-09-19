import React, { useEffect, useState } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  Alert, Switch, ActivityIndicator,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { APP_VERSION, COLORS } from '@/config';
import { FEATURES } from '@/config/features';
import { notificationsApi } from '@/api/services';

interface SettingRowProps {
  icon: string;
  title: string;
  subtitle?: string;
  onPress?: () => void;
  rightElement?: React.ReactNode;
  destructive?: boolean;
}

function SettingRow({ icon, title, subtitle, onPress, rightElement, destructive }: SettingRowProps) {
  return (
    <TouchableOpacity
      onPress={onPress}
      disabled={!onPress && !rightElement}
      style={{
        flexDirection: 'row', alignItems: 'center', padding: 16,
        borderBottomWidth: 1, borderBottomColor: '#F3F4F6',
      }}
    >
      <View style={{
        width: 36, height: 36, borderRadius: 10, alignItems: 'center', justifyContent: 'center',
        backgroundColor: destructive ? '#FEF2F2' : '#F3F4F6', marginRight: 14,
      }}>
        <Text style={{ fontSize: 18 }}>{icon}</Text>
      </View>
      <View style={{ flex: 1 }}>
        <Text style={{ fontSize: 15, fontWeight: '500', color: destructive ? '#DC2626' : '#111827' }}>
          {title}
        </Text>
        {subtitle && <Text style={{ fontSize: 12, color: '#9CA3AF', marginTop: 1 }}>{subtitle}</Text>}
      </View>
      {rightElement ? rightElement : onPress ? (
        <Text style={{ color: '#D1D5DB', fontSize: 18 }}>›</Text>
      ) : null}
    </TouchableOpacity>
  );
}

function SectionHeader({ title }: { title: string }) {
  return (
    <Text style={{ fontSize: 11, fontWeight: '700', color: '#9CA3AF', paddingHorizontal: 16, paddingTop: 20, paddingBottom: 6, textTransform: 'uppercase' }}>
      {title}
    </Text>
  );
}

export default function SettingsScreen() {
  const navigation = useNavigation<any>();
  const { i18n } = useTranslation();
  const { user, logout } = useAuthStore();
  const [biometricEnabled, setBiometricEnabled] = useState(false);
  const [pushEnabled, setPushEnabled] = useState(false);
  const [pushSaving, setPushSaving] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  useEffect(() => {
    let active = true;
    if (!FEATURES.NOTIFICATION_PREFERENCES) return () => { active = false; };
    notificationsApi
      .getChannelSwitches()
      .then((settings) => active && setPushEnabled(settings.pushEnabled))
      .catch((error) => console.warn('[Settings] Notification preferences unavailable:', error));
    return () => { active = false; };
  }, []);

  const togglePush = async (enabled: boolean) => {
    const previous = pushEnabled;
    setPushEnabled(enabled);
    setPushSaving(true);
    try {
      const saved = await notificationsApi.setPushEnabled(enabled);
      setPushEnabled(saved.pushEnabled);
    } catch (error: any) {
      setPushEnabled(previous);
      Alert.alert('Could not update notifications', error?.message || 'Please try again.');
    } finally {
      setPushSaving(false);
    }
  };

  const toggleLanguage = async () => {
    const newLang = i18n.language === 'ar' ? 'en' : 'ar';
    await i18n.changeLanguage(newLang);
    // Note: RTL change requires app restart in production
    // I18nManager.forceRTL(newLang === 'ar');
    Alert.alert(
      'Language Changed',
      newLang === 'ar' ? 'تم التغيير إلى العربية' : 'Changed to English',
    );
  };

  const handleLogout = () => {
    Alert.alert(
      'Sign Out',
      'Are you sure you want to sign out?',
      [
        { text: 'Cancel', style: 'cancel' },
        {
          text: 'Sign Out',
          style: 'destructive',
          onPress: async () => {
            setLoggingOut(true);
            await logout();
            setLoggingOut(false);
          },
        },
      ]
    );
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Settings</Text>
      </View>

      {/* Profile summary */}
      <View style={{
        backgroundColor: '#fff', padding: 16, flexDirection: 'row', alignItems: 'center', gap: 14,
        borderBottomWidth: 1, borderBottomColor: '#F3F4F6',
      }}>
        <View style={{
          width: 50, height: 50, borderRadius: 25, backgroundColor: COLORS.blue,
          alignItems: 'center', justifyContent: 'center',
        }}>
          <Text style={{ color: '#fff', fontSize: 20, fontWeight: '700' }}>
            {(user?.name ?? 'U').charAt(0).toUpperCase()}
          </Text>
        </View>
        <View>
          <Text style={{ fontSize: 16, fontWeight: '700', color: '#111827' }}>{user?.name}</Text>
          <Text style={{ fontSize: 13, color: '#6B7280' }}>{user?.email}</Text>
          <Text style={{ fontSize: 11, color: '#9CA3AF', marginTop: 2 }}>{user?.role} · {user?.department}</Text>
        </View>
      </View>

      <ScrollView contentContainerStyle={{ paddingBottom: 40 }}>
        {/* Account */}
        <SectionHeader title="Account" />
        <View style={{ backgroundColor: '#fff', marginHorizontal: 16, borderRadius: 14, overflow: 'hidden' }}>
          <SettingRow
            icon="🔑"
            title="Change Password"
            subtitle="Update your login password"
            onPress={() => navigation.navigate('ChangePassword')}
          />
          <SettingRow
            icon="👆"
            title="Biometric Login"
            subtitle={FEATURES.BIOMETRIC_LOGIN ? 'Use Face ID / Fingerprint to sign in' : 'Coming soon'}
            rightElement={
              <Switch
                value={FEATURES.BIOMETRIC_LOGIN && biometricEnabled}
                disabled={!FEATURES.BIOMETRIC_LOGIN}
                onValueChange={setBiometricEnabled}
                trackColor={{ false: '#D1D5DB', true: COLORS.blue }}
                thumbColor="#fff"
              />
            }
          />
        </View>

        {/* Preferences */}
        <SectionHeader title="Preferences" />
        <View style={{ backgroundColor: '#fff', marginHorizontal: 16, borderRadius: 14, overflow: 'hidden' }}>
          <SettingRow
            icon="🌐"
            title="Language"
            subtitle={i18n.language === 'ar' ? 'العربية' : 'English'}
            onPress={toggleLanguage}
            rightElement={
              <View style={{
                backgroundColor: '#EFF6FF', borderRadius: 8,
                paddingHorizontal: 10, paddingVertical: 4,
              }}>
                <Text style={{ color: COLORS.blue, fontSize: 13, fontWeight: '600' }}>
                  {i18n.language === 'ar' ? 'عربي → EN' : 'EN → عربي'}
                </Text>
              </View>
            }
          />
          <SettingRow
            icon="🔔"
            title="Push Notifications"
            subtitle={
              FEATURES.NOTIFICATION_PREFERENCES
                ? 'Receive real-time alerts'
                : 'Managed in your device Settings › KynexOne › Notifications'
            }
            rightElement={
              <Switch
                // Per-user preferences need GET/PUT /ess/notification-preferences (not built).
                // The toggle used to flip local state only, silently doing nothing.
                value={pushEnabled}
                disabled={!FEATURES.NOTIFICATION_PREFERENCES || pushSaving}
                onValueChange={togglePush}
                trackColor={{ false: '#D1D5DB', true: COLORS.blue }}
                thumbColor="#fff"
              />
            }
          />
          <SettingRow
            icon="📅"
            title="Calendar"
            subtitle="Hijri / Gregorian · Gregorian (default)"
            onPress={() => Alert.alert('Coming Soon', 'Hijri calendar support coming in a future update')}
          />
        </View>

        {/* About */}
        <SectionHeader title="About" />
        <View style={{ backgroundColor: '#fff', marginHorizontal: 16, borderRadius: 14, overflow: 'hidden' }}>
          <SettingRow
            icon="ℹ️"
            title="App Version"
            subtitle={`${APP_VERSION} (build ${String(require('../../../app.json').expo.ios?.buildNumber ?? 'dev')})`}
          />
          <SettingRow
            icon="📋"
            title="Privacy Policy"
            onPress={() => Alert.alert('Privacy Policy', 'Contact your HR department for privacy policy details')}
          />
          <SettingRow
            icon="💬"
            title="Support"
            subtitle="Contact HR support"
            onPress={() => Alert.alert('Support', 'Please contact your HR department for support')}
          />
        </View>

        {/* Sign out */}
        <SectionHeader title="Session" />
        <View style={{ backgroundColor: '#fff', marginHorizontal: 16, borderRadius: 14, overflow: 'hidden' }}>
          <SettingRow
            icon="🚪"
            title={loggingOut ? 'Signing out...' : 'Sign Out'}
            destructive
            onPress={loggingOut ? undefined : handleLogout}
            rightElement={loggingOut ? <ActivityIndicator color="#DC2626" size="small" /> : undefined}
          />
        </View>

        {/* Tenant / device info */}
        <View style={{ marginHorizontal: 16, marginTop: 20, alignItems: 'center' }}>
          <Text style={{ fontSize: 11, color: '#D1D5DB' }}>
            Employee #{user?.employeeId} · Tenant {user?.tenantId}
          </Text>
          <Text style={{ fontSize: 10, color: '#E5E7EB', marginTop: 2 }}>
            KynexOne Workforce Platform
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}
