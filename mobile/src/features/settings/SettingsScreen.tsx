import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Linking,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  View,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { APP_VERSION } from '@/config';
import { FEATURES } from '@/config/features';
import { notificationsApi } from '@/api/services';
import { useTheme } from '@/theme/ThemeProvider';
import type { ThemePreference } from '@/theme/tokens';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';

interface SettingRowProps {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  title: string;
  subtitle?: string;
  onPress?: () => void;
  rightElement?: React.ReactNode;
  destructive?: boolean;
  isLast?: boolean;
}

export default function SettingsScreen() {
  const navigation = useNavigation<any>();
  const { i18n } = useTranslation();
  const { user, logout } = useAuthStore();
  const { theme, preference, setPreference } = useTheme();
  const [pushEnabled, setPushEnabled] = useState(false);
  const [pushSaving, setPushSaving] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  useEffect(() => {
    let active = true;
    if (!FEATURES.NOTIFICATION_PREFERENCES) return () => { active = false; };

    notificationsApi
      .getChannelSwitches()
      .then((settings) => {
        if (active) setPushEnabled(settings.pushEnabled);
      })
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
      Alert.alert('Could not update notifications', error?.message ?? 'Please try again.');
    } finally {
      setPushSaving(false);
    }
  };

  const toggleLanguage = async () => {
    const next = i18n.language === 'ar' ? 'en' : 'ar';
    await i18n.changeLanguage(next);
    Alert.alert(
      'Language changed',
      next === 'ar'
        ? 'تم التغيير إلى العربية. سيكتمل اتجاه الواجهة بعد إعادة تشغيل التطبيق.'
        : 'Changed to English. Layout direction completes after an app restart.',
    );
  };

  const handleLogout = () => {
    Alert.alert('Sign out', 'End this secure session on this device?', [
      { text: 'Cancel', style: 'cancel' },
      {
        text: 'Sign out',
        style: 'destructive',
        onPress: async () => {
          setLoggingOut(true);
          await logout();
          setLoggingOut(false);
        },
      },
    ]);
  };
  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero
          eyebrow="Preferences"
          title="Settings"
          subtitle="Security, appearance and communication controls"
        />

        <View style={styles.section}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.profileCard}>
            <View style={[styles.avatar, { backgroundColor: theme.colors.primary }]}>
              <Text style={styles.avatarText}>
                {(user?.name ?? user?.fullName ?? 'U').charAt(0).toUpperCase()}
              </Text>
            </View>
            <View style={styles.profileCopy}>
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>
                {user?.name ?? user?.fullName}
              </Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 2 }]}>
                {user?.email}
              </Text>
              <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 4 }]}>
                {user?.role}{user?.department ? ` · ${user.department}` : ''}
              </Text>
            </View>
            <View style={[styles.securePill, { backgroundColor: `${theme.colors.success}1A` }]}>
              <Ionicons name="shield-checkmark" size={14} color={theme.colors.success} />
              <Text style={[theme.typography.micro, { color: theme.colors.success }]}>Secure</Text>
            </View>
          </GlassSurface>
        </View>

        <View style={styles.section}>
          <SectionHeader title="Appearance" subtitle="Liquid Glass adapts to your preference" />
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.appearanceCard}>
            <ThemeSelector
              value={preference}
              onChange={(next) => void setPreference(next)}
            />
          </GlassSurface>
        </View>
        <SettingsSection title="Account">
          <SettingRow
            icon="key-outline"
            title="Change password"
            subtitle="Update your workforce account password"
            onPress={() => navigation.navigate('ChangePassword')}
          />
          <SettingRow
            icon="finger-print-outline"
            title="Biometric access"
            subtitle={
              FEATURES.BIOMETRIC_LOGIN
                ? 'Use Face ID or device biometrics'
                : 'Protected biometric sign-in is being finalized'
            }
            onPress={FEATURES.BIOMETRIC_LOGIN ? () => undefined : undefined}
            isLast
          />
        </SettingsSection>

        <SettingsSection title="Preferences">
          <SettingRow
            icon="language-outline"
            title="Language"
            subtitle={i18n.language === 'ar' ? 'العربية' : 'English'}
            onPress={toggleLanguage}
            rightElement={
              <View style={[styles.languagePill, { backgroundColor: `${theme.colors.primary}18` }]}>
                <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
                  {i18n.language === 'ar' ? 'عربي → EN' : 'EN → عربي'}
                </Text>
              </View>
            }
          />
          <SettingRow
            icon="notifications-outline"
            title="Push notifications"
            subtitle={
              FEATURES.NOTIFICATION_PREFERENCES
                ? 'Real-time alerts and request updates'
                : 'Managed in device notification settings'
            }
            rightElement={
              pushSaving ? (
                <ActivityIndicator color={theme.colors.primary} size="small" />
              ) : (
                <Switch
                  value={pushEnabled}
                  disabled={!FEATURES.NOTIFICATION_PREFERENCES}
                  onValueChange={(next) => void togglePush(next)}
                  trackColor={{ false: theme.colors.border, true: theme.colors.primary }}
                  thumbColor="#FFFFFF"
                />
              )
            }
          />
          <SettingRow
            icon="calendar-clear-outline"
            title="Calendar"
            subtitle="Gregorian default · Hijri support planned"
            onPress={() => Alert.alert('Calendar', 'Hijri calendar selection is being prepared for a future release.')}
            isLast
          />
        </SettingsSection>
        <SettingsSection title="About">
          <SettingRow
            icon="information-circle-outline"
            title="App version"
            subtitle={`${APP_VERSION} · Build ${String(require('../../../app.json').expo.ios?.buildNumber ?? 'dev')}`}
          />
          <SettingRow
            icon="lock-closed-outline"
            title="Privacy policy"
            subtitle="How KynexOne protects workforce data"
            onPress={() => Alert.alert('Privacy policy', 'The published privacy-policy URL will be linked before store submission.')}
          />
          <SettingRow
            icon="help-buoy-outline"
            title="Support"
            subtitle="Contact your HR support team"
            onPress={() => Alert.alert('Support', 'Contact your HR department or organization support desk.')}
          />
          <SettingRow
            icon="options-outline"
            title="Device settings"
            subtitle="Permissions, notifications and biometrics"
            onPress={() => void Linking.openSettings()}
            isLast
          />
        </SettingsSection>

        <SettingsSection title="Session">
          <SettingRow
            icon="log-out-outline"
            title={loggingOut ? 'Signing out…' : 'Sign out'}
            subtitle="Remove the secure session from this device"
            destructive
            onPress={loggingOut ? undefined : handleLogout}
            rightElement={
              loggingOut ? <ActivityIndicator color={theme.colors.danger} size="small" /> : undefined
            }
            isLast
          />
        </SettingsSection>

        <View style={styles.tenantInfo}>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
            Employee #{user?.employeeId ?? '—'} · Tenant {user?.tenantId ?? '—'}
          </Text>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 3 }]}>
            KynexOne Workforce Platform
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}
function ThemeSelector({
  value,
  onChange,
}: {
  value: ThemePreference;
  onChange: (value: ThemePreference) => void;
}) {
  const { theme } = useTheme();
  const options: {
    value: ThemePreference;
    label: string;
    icon: React.ComponentProps<typeof Ionicons>['name'];
  }[] = [
    { value: 'system', label: 'System', icon: 'phone-portrait-outline' },
    { value: 'light', label: 'Light', icon: 'sunny-outline' },
    { value: 'dark', label: 'Dark', icon: 'moon-outline' },
  ];

  return (
    <View style={styles.themeRow}>
      {options.map((option) => {
        const selected = value === option.value;
        return (
          <MotionPressable
            key={option.value}
            onPress={() => onChange(option.value)}
            haptic="selection"
            style={styles.themeOptionShell}
            contentStyle={[
              styles.themeOption,
              {
                backgroundColor: selected ? `${theme.colors.primary}1F` : theme.colors.surfaceSoft,
                borderColor: selected ? theme.colors.primary : theme.colors.border,
              },
            ]}
            accessibilityRole="radio"
            accessibilityState={{ selected }}
          >
            <Ionicons
              name={option.icon}
              size={19}
              color={selected ? theme.colors.primary : theme.colors.textMuted}
            />
            <Text
              style={[
                theme.typography.caption,
                {
                  color: selected ? theme.colors.text : theme.colors.textSecondary,
                  fontWeight: selected ? '700' : '500',
                },
              ]}
            >
              {option.label}
            </Text>
          </MotionPressable>
        );
      })}
    </View>
  );
}
function SettingsSection({ title, children }: { title: string; children: React.ReactNode }) {
  const { theme } = useTheme();
  return (
    <View style={styles.section}>
      <SectionHeader title={title} />
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.rowsCard}>
        {children}
      </GlassSurface>
    </View>
  );
}

function SettingRow({
  icon,
  title,
  subtitle,
  onPress,
  rightElement,
  destructive,
  isLast,
}: SettingRowProps) {
  const { theme } = useTheme();
  const accent = destructive ? theme.colors.danger : theme.colors.primary;

  return (
    <MotionPressable
      onPress={onPress}
      disabled={!onPress && !rightElement}
      haptic={onPress ? 'selection' : 'none'}
      contentStyle={styles.rowPressable}
      accessibilityRole={onPress ? 'button' : undefined}
    >
      <View
        style={[
          styles.settingRow,
          !isLast && {
            borderBottomColor: theme.colors.divider,
            borderBottomWidth: StyleSheet.hairlineWidth,
          },
        ]}
      >
        <View style={[styles.settingIcon, { backgroundColor: `${accent}17` }]}>
          <Ionicons name={icon} size={20} color={accent} />
        </View>
        <View style={styles.settingCopy}>
          <Text
            style={[
              theme.typography.bodyStrong,
              { color: destructive ? theme.colors.danger : theme.colors.text },
            ]}
          >
            {title}
          </Text>
          {subtitle ? (
            <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
              {subtitle}
            </Text>
          ) : null}
        </View>
        {rightElement ?? (onPress ? (
          <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
        ) : null)}
      </View>
    </MotionPressable>
  );
}
const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 38 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  profileCard: { flexDirection: 'row', alignItems: 'center', gap: 13, padding: 16 },
  avatar: {
    width: 52,
    height: 52,
    borderRadius: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  avatarText: { color: '#FFFFFF', fontSize: 21, fontWeight: '800' },
  profileCopy: { flex: 1, minWidth: 0 },
  securePill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 5,
    paddingHorizontal: 9,
    paddingVertical: 6,
    borderRadius: 999,
  },
  appearanceCard: { padding: 12 },
  themeRow: { flexDirection: 'row', gap: 8 },
  themeOptionShell: { flex: 1 },
  themeOption: {
    minHeight: 72,
    borderRadius: 17,
    borderWidth: StyleSheet.hairlineWidth,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
  },
  rowsCard: { paddingHorizontal: 14 },
  rowPressable: { borderRadius: 18 },
  settingRow: {
    minHeight: 72,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 11,
  },
  settingIcon: {
    width: 42,
    height: 42,
    borderRadius: 15,
    alignItems: 'center',
    justifyContent: 'center',
  },
  settingCopy: { flex: 1, minWidth: 0 },
  languagePill: { paddingHorizontal: 10, paddingVertical: 6, borderRadius: 11 },
  tenantInfo: { marginTop: 24, alignItems: 'center', paddingHorizontal: 16 },
});
