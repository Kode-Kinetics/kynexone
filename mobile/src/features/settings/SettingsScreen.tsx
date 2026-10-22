import React, { useCallback, useEffect, useState } from 'react';
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
import { useFocusEffect, useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { reloadAppAsync } from 'expo';
import { setLanguage } from '@/config/i18n';
import { useAuthStore } from '@/auth/authStore';
import { APP_VERSION } from '@/config';
import { FEATURES } from '@/config/features';
import { notificationsApi, selfieAttendanceApi } from '@/api/services';
import type { AttendanceVerification } from '@/features/attendance/selfieAttendance';
import { useTheme } from '@/theme/ThemeProvider';
import type { ThemePreference } from '@/theme/tokens';
import {
  DirectionalIcon,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SegmentedControl,
  Surface,
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

const BUILD_NUMBER = String(require('../../../app.json').expo.ios?.buildNumber ?? '');

export default function SettingsScreen() {
  const navigation = useNavigation<any>();
  const { t, i18n } = useTranslation();
  const { user, logout } = useAuthStore();
  const { theme, preference, setPreference } = useTheme();
  const [pushEnabled, setPushEnabled] = useState(false);
  const [pushSaving, setPushSaving] = useState(false);
  const [pushError, setPushError] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  const [verification, setVerification] = useState<AttendanceVerification | null>(null);
  const arabic = i18n.language?.startsWith('ar');
  const name = (arabic ? user?.fullNameAr : undefined) || user?.fullName || user?.email || '';

  // Selfie consent must stay reachable (to withdraw) whenever the tenant uses selfies or a consent is on record.
  useFocusEffect(useCallback(() => {
    let active = true;
    selfieAttendanceApi.getVerification()
      .then((value) => { if (active) setVerification(value); })
      .catch((error) => {
        if (__DEV__) console.warn('[Settings] Selfie attendance settings unavailable:', error);
      });
    return () => { active = false; };
  }, []));
  const showSelfieRow = verification != null && (verification.selfie.enabled || verification.selfie.consent != null);

  useEffect(() => {
    let active = true;
    if (!FEATURES.NOTIFICATION_PREFERENCES) return () => { active = false; };
    notificationsApi
      .getChannelSwitches()
      .then((settings) => {
        if (active) setPushEnabled(settings.pushEnabled);
      })
      .catch((error) => {
        if (__DEV__) console.warn('[Settings] Notification preferences unavailable:', error);
      });
    return () => { active = false; };
  }, []);

  const togglePush = async (enabled: boolean) => {
    const previous = pushEnabled;
    setPushEnabled(enabled);
    setPushSaving(true);
    setPushError(false);
    try {
      const saved = await notificationsApi.setPushEnabled(enabled);
      setPushEnabled(saved.pushEnabled);
    } catch {
      setPushEnabled(previous);
      setPushError(true);
    } finally {
      setPushSaving(false);
    }
  };

  const toggleLanguage = async () => {
    const newLang = arabic ? 'en' : 'ar';
    // Translates now, persists the choice, and sets the native direction (config/i18n.ts).
    const { restartRequired } = await setLanguage(newLang);
    if (!restartRequired) {
      Alert.alert(t('settings.languageChanged'), newLang === 'ar' ? t('settings.changedToArabic') : t('settings.changedToEnglish'));
      return;
    }
    // React Native fixes the layout direction per process: offer the restart rather than leave it half-flipped.
    Alert.alert(t('settings.restartTitle'), t('settings.restartBody'), [
      { text: t('settings.later'), style: 'cancel' },
      {
        text: t('settings.restartNow'),
        onPress: () => {
          reloadAppAsync('Language direction changed').catch((error) => {
            if (__DEV__) console.warn('[Settings] Reload failed; the direction applies on next launch:', error);
          });
        },
      },
    ]);
  };

  const handleLogout = () => {
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

  const themeSegments: { key: ThemePreference; label: string }[] = [
    { key: 'system', label: t('account.settings.themeSystem') },
    { key: 'light', label: t('account.settings.themeLight') },
    { key: 'dark', label: t('account.settings.themeDark') },
  ];

  const version = BUILD_NUMBER
    ? t('account.settings.versionWithBuild', { version: APP_VERSION, build: BUILD_NUMBER })
    : APP_VERSION;

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero
          eyebrow={t('account.settings.eyebrow')}
          title={t('account.settings.title')}
          subtitle={t('account.settings.subtitle')}
        />

        <View style={styles.section}>
          <Surface contentStyle={styles.profileCard}>
            <View style={[styles.avatar, { backgroundColor: theme.colors.primaryFill }]}>
              <Text style={[theme.typography.title3, { color: theme.colors.onFill }]}>
                {name.trim().charAt(0).toUpperCase()}
              </Text>
            </View>
            <View style={styles.flex}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{name}</Text>
              <Text style={[theme.typography.footnote, styles.subline, { color: theme.colors.textSecondary }]}>
                {user?.email}
              </Text>
            </View>
          </Surface>
        </View>

        <View style={styles.section}>
          <SectionHeader title={t('account.settings.appearance')} />
          <SegmentedControl
            segments={themeSegments}
            value={preference}
            onChange={(next) => void setPreference(next)}
          />
        </View>

        <SettingsSection title={t('account.settings.security')}>
          <SettingRow
            icon="key-outline"
            title={t('account.password.title')}
            subtitle={t('account.settings.passwordSub')}
            onPress={() => navigation.navigate('ChangePassword')}
            isLast
          />
        </SettingsSection>

        <SettingsSection title={t('account.settings.preferences')}>
          <SettingRow
            icon="language-outline"
            title={t('settings.language')}
            subtitle={arabic ? 'العربية' : 'English'}
            onPress={toggleLanguage}
            rightElement={
              <Text style={[theme.typography.subheadline, styles.languageSwitch, { color: theme.colors.accent }]}>
                {arabic ? 'English' : 'العربية'}
              </Text>
            }
          />
          <SettingRow
            icon="notifications-outline"
            title={t('account.settings.push')}
            subtitle={pushError
              ? t('account.settings.pushFailed')
              : FEATURES.NOTIFICATION_PREFERENCES
                ? t('account.settings.pushSub')
                : t('account.settings.pushDevice')}
            rightElement={
              pushSaving ? (
                <ActivityIndicator color={theme.colors.accent} size="small" />
              ) : (
                <Switch
                  accessibilityLabel={t('account.settings.push')}
                  value={pushEnabled}
                  disabled={!FEATURES.NOTIFICATION_PREFERENCES}
                  onValueChange={(next) => void togglePush(next)}
                  trackColor={{ false: theme.colors.border, true: theme.colors.primaryFill }}
                  thumbColor={theme.colors.onFill}
                />
              )
            }
            isLast={!showSelfieRow}
          />
          {showSelfieRow ? (
            <SettingRow
              icon="camera-outline"
              title={t('selfie.settingsRow.title')}
              subtitle={verification?.selfie.consent ? t('selfie.settingsRow.on') : t('selfie.settingsRow.off')}
              onPress={() => navigation.navigate('SelfieConsent')}
              isLast
            />
          ) : null}
        </SettingsSection>

        <SettingsSection title={t('account.settings.about')}>
          <SettingRow icon="information-circle-outline" title={t('account.settings.version')} subtitle={version} />
          <SettingRow
            icon="help-buoy-outline"
            title={t('account.settings.help')}
            subtitle={t('account.settings.helpSub')}
          />
          <SettingRow
            icon="options-outline"
            title={t('account.settings.deviceSettings')}
            subtitle={t('account.settings.deviceSettingsSub')}
            onPress={() => void Linking.openSettings()}
            isLast
          />
        </SettingsSection>

        <SettingsSection title={t('more.session')}>
          <SettingRow
            icon="log-out-outline"
            title={loggingOut ? t('more.signingOut') : t('more.signOut')}
            subtitle={t('more.signOutBody')}
            destructive
            onPress={loggingOut ? undefined : handleLogout}
            rightElement={loggingOut ? <ActivityIndicator color={theme.colors.status.danger.fg} size="small" /> : undefined}
            isLast
          />
        </SettingsSection>
      </ScrollView>
    </View>
  );
}

function SettingsSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <SectionHeader title={title} />
      <Surface contentStyle={styles.rowsCard}>{children}</Surface>
    </View>
  );
}

function SettingRow({ icon, title, subtitle, onPress, rightElement, destructive, isLast }: SettingRowProps) {
  const { theme } = useTheme();
  const tone = destructive ? theme.colors.status.danger : null;

  const body = (
    <View
      style={[
        styles.settingRow,
        !isLast && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth },
      ]}
    >
      <View style={[styles.settingIcon, { backgroundColor: tone ? tone.tint : theme.colors.surfaceInset }]}>
        <Ionicons accessible={false} name={icon} size={20} color={tone ? tone.fg : theme.colors.accent} />
      </View>
      <View style={styles.flex}>
        <Text style={[theme.typography.callout, styles.rowTitle, { color: tone ? tone.fg : theme.colors.text }]}>{title}</Text>
        {subtitle ? (
          <Text style={[theme.typography.footnote, styles.subline, { color: theme.colors.textSecondary }]}>{subtitle}</Text>
        ) : null}
      </View>
      {rightElement ?? (onPress ? <DirectionalIcon name="chevron" size={18} color={theme.colors.textMuted} /> : null)}
    </View>
  );

  if (!onPress) {
    // Information rows are read as one sentence; rows holding a control leave the control focusable.
    return rightElement ? body : (
      <View accessible accessibilityLabel={[title, subtitle].filter(Boolean).join('. ')}>{body}</View>
    );
  }
  return (
    <MotionPressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityLabel={[title, subtitle].filter(Boolean).join('. ')}
    >
      {body}
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 32 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  profileCard: { flexDirection: 'row', alignItems: 'center', gap: 14, padding: 16 },
  avatar: { width: 52, height: 52, borderRadius: 26, alignItems: 'center', justifyContent: 'center' },
  flex: { flex: 1, minWidth: 0 },
  subline: { marginTop: 2 },
  rowsCard: { paddingHorizontal: 14 },
  settingRow: { minHeight: 64, flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 10 },
  settingIcon: { width: 38, height: 38, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  rowTitle: { fontWeight: '600' },
  languageSwitch: { fontWeight: '600' },
});
