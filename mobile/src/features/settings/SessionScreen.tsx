import React, { useState } from 'react';
import { Alert, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { deriveMobileAccess } from '@/auth/accessPolicy';
import {
  LiquidBackdrop,
  LiquidButton,
  ScreenHero,
  StatusPill,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { AccessMode } from '@/types';

/** Plain-language name for each server access mode (account.session.modes.*). */
const MODE_KEYS = {
  FullPortal: 'full',
  ESSOnly: 'selfService',
  ManagerPortal: 'manager',
  HRPortal: 'hr',
  PayrollPortal: 'payroll',
  FinancePortal: 'finance',
  SupervisorPortal: 'supervisor',
  ReadOnlyAuditor: 'readOnly',
  Mobile: 'mobile',
  KioskOnly: 'kiosk',
  NoLogin: 'none',
} as const satisfies Record<AccessMode, string>;

/**
 * Account: who is signed in, to which company, with what kind of access — and sign out. Also the
 * landing screen for logins whose access opens nothing else on mobile (auditors, kiosks, no-login).
 */
export default function SessionScreen() {
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const { user, logout } = useAuthStore();
  const [loggingOut, setLoggingOut] = useState(false);
  const policy = deriveMobileAccess(user);
  const name = (i18n.language?.startsWith('ar') ? user?.fullNameAr : undefined) || user?.fullName || user?.email || '';
  const initial = name.trim().charAt(0).toUpperCase();
  const onlyAccount = policy.surfaces.size <= 1;

  const signOut = () => {
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

  const rows: [string, string][] = [
    [t('account.session.email'), user?.email ?? ''],
    ...(user?.tenantSlug ? [[t('account.session.company'), user.tenantSlug] as [string, string]] : []),
    [t('account.session.access'), t(`account.session.modes.${MODE_KEYS[policy.mode]}`)],
  ];

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero
          eyebrow={t('account.session.eyebrow')}
          title={t('account.session.title')}
          subtitle={t('account.session.subtitle')}
        />
        <View style={styles.section}>
          <Surface contentStyle={styles.identity}>
            <View style={[styles.avatar, { backgroundColor: theme.colors.primaryFill }]}>
              <Text style={[theme.typography.title2, { color: theme.colors.onFill }]}>{initial}</Text>
            </View>
            <View style={styles.flex}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{name}</Text>
              {policy.readOnly && policy.mode !== 'NoLogin' ? (
                <StatusPill tone="info" label={t('account.session.readOnly')} style={styles.pill} />
              ) : null}
            </View>
          </Surface>

          <Surface contentStyle={styles.table}>
            {rows.map(([label, value], index) => (
              <View
                key={label}
                style={[
                  styles.row,
                  index < rows.length - 1 && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth },
                ]}
              >
                <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{label}</Text>
                <Text selectable style={[theme.typography.callout, { color: theme.colors.text }]}>{value}</Text>
              </View>
            ))}
          </Surface>

          {onlyAccount ? (
            <Text style={[theme.typography.footnote, styles.note, { color: theme.colors.textSecondary }]}>
              {t('account.session.nothingElse')}
            </Text>
          ) : null}

          <LiquidButton
            label={loggingOut ? t('more.signingOut') : t('more.signOut')}
            icon="log-out-outline"
            variant="danger"
            onPress={signOut}
            loading={loggingOut}
            disabled={loggingOut}
            accessibilityHint={t('more.signOutBody')}
          />
        </View>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 32 },
  section: { paddingHorizontal: 16, gap: 12 },
  identity: { flexDirection: 'row', alignItems: 'center', gap: 14, padding: 16 },
  avatar: { width: 56, height: 56, borderRadius: 28, alignItems: 'center', justifyContent: 'center' },
  flex: { flex: 1, minWidth: 0, gap: 6 },
  pill: { marginTop: 2 },
  table: { paddingHorizontal: 16 },
  row: { paddingVertical: 12, gap: 2 },
  note: { paddingHorizontal: 4 },
});
