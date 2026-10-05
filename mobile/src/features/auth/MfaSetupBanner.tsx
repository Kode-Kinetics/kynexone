import React, { useEffect, useState } from 'react';
import { ActivityIndicator, AppState, Modal, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import * as ScreenCapture from 'expo-screen-capture';
import { authApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { classifyMfaFailure, formatEnforceDate, type MfaErrorKind } from '@/auth/mfaFlow';
import { COLORS } from '@/config';
import { MfaEnrollmentView } from './MfaEnrollmentView';
import { createSecureSheet } from './secureSheet';

// Bottom tab bars are a fixed 72 pt (MainTabs tabScreenOptions); float just above them.
const TAB_BAR_HEIGHT = 72;

/**
 * Signed-in prompt for privileged users who have not set up two-step sign-in.
 * Grace period: dismissible for this session. Enforced: stays until done.
 * Tapping it starts enrolment in a full-screen sheet, which on success ends
 * the session (the server rotates the session stamp) and returns to sign-in.
 */
export function MfaSetupBanner() {
  const { t, i18n } = useTranslation();
  const insets = useSafeAreaInsets();
  const prompt = useAuthStore((s) => s.mfaPrompt);
  const dismissed = useAuthStore((s) => s.mfaPromptDismissed);
  const dismiss = useAuthStore((s) => s.dismissMfaPrompt);
  const refreshMfaStatus = useAuthStore((s) => s.refreshMfaStatus);
  const endSession = useAuthStore((s) => s.endSessionAfterMfaEnrollment);
  const user = useAuthStore((s) => s.user);
  const tenantId = useAuthStore((s) => s.tenantId);

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

  if (!prompt || prompt.kind === 'none' || !user || !tenantId) return null;
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
        <View style={[styles.wrap, { bottom: TAB_BAR_HEIGHT + Math.max(insets.bottom, 0) + 10 }]} pointerEvents="box-none">
          <View style={styles.card}>
            <TouchableOpacity
              style={styles.main}
              onPress={() => void start()}
              disabled={starting}
              accessibilityRole="button"
              accessibilityHint={t('mfa.bannerAction')}
            >
              <Ionicons name="shield-half-outline" size={22} color={COLORS.warning} />
              <View style={styles.textCol}>
                <Text style={styles.message}>{message}</Text>
                {startError ? (
                  <Text style={styles.error}>
                    {startError === 'alreadyOn' ? t('mfa.alreadyOn') : t(`mfa.errors.${startError}`)}
                  </Text>
                ) : null}
              </View>
            </TouchableOpacity>
            <View style={styles.actions}>
              {!enforced ? (
                <TouchableOpacity onPress={dismiss} style={styles.secondary} accessibilityRole="button">
                  <Text style={styles.secondaryText}>{t('mfa.bannerDismiss')}</Text>
                </TouchableOpacity>
              ) : null}
              <TouchableOpacity
                onPress={() => void start()}
                disabled={starting}
                style={styles.primary}
                accessibilityRole="button"
                accessibilityState={{ busy: starting }}
              >
                {starting ? <ActivityIndicator color="#fff" size="small" /> : <Text style={styles.primaryText}>{t('mfa.bannerAction')}</Text>}
              </TouchableOpacity>
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
            tenantId={tenantId}
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

const styles = StyleSheet.create({
  wrap: { position: 'absolute', start: 12, end: 12 },
  card: {
    backgroundColor: COLORS.navy,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: 'rgba(245,158,11,0.45)',
    padding: 14,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 6 },
    shadowOpacity: 0.25,
    shadowRadius: 12,
    elevation: 10,
  },
  main: { flexDirection: 'row', alignItems: 'flex-start', gap: 10 },
  textCol: { flex: 1 },
  message: { color: '#fff', fontSize: 14, lineHeight: 20, fontWeight: '600', textAlign: 'auto' },
  error: { color: '#FCA5A5', fontSize: 12, lineHeight: 17, marginTop: 6, textAlign: 'auto' },
  actions: { flexDirection: 'row', justifyContent: 'flex-end', alignItems: 'center', gap: 8, marginTop: 12 },
  secondary: { paddingVertical: 9, paddingHorizontal: 12 },
  secondaryText: { color: 'rgba(255,255,255,0.7)', fontSize: 13, fontWeight: '600' },
  primary: { backgroundColor: COLORS.blue, borderRadius: 10, paddingVertical: 9, paddingHorizontal: 16, minWidth: 110, alignItems: 'center' },
  primaryText: { color: '#fff', fontSize: 13, fontWeight: '800' },
});
