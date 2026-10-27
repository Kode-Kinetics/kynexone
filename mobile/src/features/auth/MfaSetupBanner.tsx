import React, { useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, AppState, Modal, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import * as ScreenCapture from 'expo-screen-capture';
import { authApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { classifyMfaFailure, formatEnforceDate, type MfaErrorKind } from '@/auth/mfaFlow';
import { MotionPressable } from '@/components/ui';
import { useTabBarInset } from '@/navigation/tabBarInset';
import { useTheme } from '@/theme/ThemeProvider';
import type { KynexTheme } from '@/theme/tokens';
import { MfaEnrollmentView } from './MfaEnrollmentView';
import { createSecureSheet } from './secureSheet';


/**
 * Signed-in prompt for privileged users who have not set up two-step sign-in.
 * Grace period: dismissible for this session. Enforced: stays until done.
 * Tapping it starts enrolment in a full-screen sheet, which on success ends
 * the session (the server rotates the session stamp) and returns to sign-in.
 */
export function MfaSetupBanner() {
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const styles = useMemo(() => makeStyles(theme), [theme]);
  // An overlay laid out against the window, so it clears the floating tab bar itself.
  const tabBarInset = useTabBarInset();
  const prompt = useAuthStore((s) => s.mfaPrompt);
  const dismissed = useAuthStore((s) => s.mfaPromptDismissed);
  const dismiss = useAuthStore((s) => s.dismissMfaPrompt);
  const refreshMfaStatus = useAuthStore((s) => s.refreshMfaStatus);
  const endSession = useAuthStore((s) => s.endSessionAfterMfaEnrollment);
  const user = useAuthStore((s) => s.user);

  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<MfaErrorKind | 'alreadyOn' | null>(null);
  const [enrollment, setEnrollment] = useState<{ enrollmentToken: string; expiresInSeconds: number } | null>(null);
  // Secures the window before the Modal exists (see secureSheet.ts for why).
  const [sheet] = useState(() => createSecureSheet(ScreenCapture, setEnrollment));
  useEffect(() => () => sheet.dispose(), [sheet]);

  // Re-read the standing when the app comes back: the enforcement date may have
  // passed, or the factor may have been set up on the web, while it was away.
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next) => {
      if (next === 'active') void refreshMfaStatus();
    });
    return () => subscription.remove();
  }, [refreshMfaStatus]);

  if (!prompt || prompt.kind === 'none' || !user) return null;
  const enforced = prompt.kind === 'enforced';
  if (dismissed && !enforced && !enrollment) return null;

  const start = async () => {
    if (starting) return;
    setStarting(true);
    setStartError(null);
    try {
      await sheet.open(await authApi.startSignedInMfaEnrollment());
    } catch (error: unknown) {
      const failure = classifyMfaFailure(error);
      if (failure === 'conflict') {
        setStartError('alreadyOn');
        void refreshMfaStatus();
      } else {
        setStartError(failure === 'rateLimited' ? 'rateLimited' : failure === 'network' ? 'network' : 'server');
      }
    } finally {
      setStarting(false);
    }
  };

  const message = enforced
    ? t('mfa.bannerEnforced')
    : prompt.enforceFromUtc
      ? t('mfa.bannerWithDate', { date: formatEnforceDate(prompt.enforceFromUtc, i18n.language) })
      : t('mfa.bannerNoDate');

  return (
    <>
      {!enrollment ? (
        <View style={[styles.wrap, { bottom: tabBarInset }]} pointerEvents="box-none">
          <View style={styles.card}>
            <MotionPressable
              contentStyle={styles.main}
              onPress={() => void start()}
              disabled={starting}
              accessibilityRole="button"
              accessibilityHint={t('mfa.bannerAction')}
            >
              <Ionicons accessible={false} name="shield-half-outline" size={22} color={theme.colors.status.warning.fg} />
              <View style={styles.textCol}>
                <Text style={styles.message}>{message}</Text>
                {startError ? (
                  <Text style={styles.error}>
                    {startError === 'alreadyOn' ? t('mfa.alreadyOn') : t(`mfa.errors.${startError}`)}
                  </Text>
                ) : null}
              </View>
            </MotionPressable>
            <View style={styles.actions}>
              {!enforced ? (
                <MotionPressable onPress={dismiss} contentStyle={styles.secondary} accessibilityRole="button">
                  <Text style={styles.secondaryText}>{t('mfa.bannerDismiss')}</Text>
                </MotionPressable>
              ) : null}
              <MotionPressable
                onPress={() => void start()}
                disabled={starting}
                haptic="commit"
                contentStyle={styles.primary}
                accessibilityRole="button"
                accessibilityState={{ busy: starting }}
              >
                {starting ? <ActivityIndicator color={theme.colors.onFill} size="small" /> : <Text style={styles.primaryText}>{t('mfa.bannerAction')}</Text>}
              </MotionPressable>
            </View>
          </View>
        </View>
      ) : null}

      <Modal
        visible={!!enrollment}
        animationType="slide"
        presentationStyle="fullScreen"
        onRequestClose={() => sheet.close()}
      >
        {enrollment ? (
          <MfaEnrollmentView
            key={enrollment.enrollmentToken}
            enrollmentToken={enrollment.enrollmentToken}
            expiresInSeconds={enrollment.expiresInSeconds}
            email={user.email}
            confirmWhenDone={false}
            onCancel={() => sheet.close()}
            onRestart={() => {
              sheet.close();
              void start();
            }}
            onEnrolled={() => {
              sheet.close();
              void endSession();
            }}
          />
        ) : null}
      </Modal>
    </>
  );
}

function makeStyles(theme: KynexTheme) {
  const c = theme.colors;
  return StyleSheet.create({
    wrap: { position: 'absolute', start: 12, end: 12 },
    card: {
      backgroundColor: c.cardRaised,
      borderRadius: theme.radius.md,
      borderWidth: 1,
      borderColor: c.status.warning.fg,
      padding: 14,
      ...theme.shadows.floating,
    },
    main: { flexDirection: 'row', alignItems: 'flex-start', gap: 10 },
    textCol: { flex: 1 },
    message: { ...theme.typography.subheadline, color: c.text, fontWeight: '600', textAlign: 'auto' },
    error: { ...theme.typography.footnote, color: c.status.danger.fg, marginTop: 6, textAlign: 'auto' },
    actions: { flexDirection: 'row', justifyContent: 'flex-end', alignItems: 'center', gap: 8, marginTop: 12 },
    secondary: { minHeight: 44, justifyContent: 'center', paddingHorizontal: 12 },
    secondaryText: { ...theme.typography.footnote, color: c.textSecondary, fontWeight: '600' },
    primary: {
      backgroundColor: c.primaryFill,
      borderRadius: theme.radius.sm,
      minHeight: 44,
      justifyContent: 'center',
      paddingHorizontal: 16,
      minWidth: 110,
      alignItems: 'center',
    },
    primaryText: { ...theme.typography.footnote, color: c.onFill, fontWeight: '700' },
  });
}
