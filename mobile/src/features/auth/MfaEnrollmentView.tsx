import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Linking,
  Platform,
  ScrollView,
  Share,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import {
  disableAppSwitcherProtectionAsync,
  enableAppSwitcherProtectionAsync,
  usePreventScreenCapture,
} from 'expo-screen-capture';
import { useTranslation } from 'react-i18next';
import { authApi } from '@/api/services';
import {
  authenticatorUri,
  classifyMfaFailure,
  formatCountdown,
  groupSecret,
  MFA_CODE_LENGTH,
  secondsLeft,
  spellSecretForScreenReader,
} from '@/auth/mfaFlow';
import { DirectionalIcon, LiquidBackdrop } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { KynexTheme } from '@/theme/tokens';
import { MfaCodeError, MfaCodeInput, useCodeEntry } from './mfaCodeEntry';

export interface MfaEnrollmentViewProps {
  enrollmentToken: string;
  expiresInSeconds: number;
  tenantId: string;
  email: string;
  /** Leave without enrolling (back to sign-in, or close the signed-in sheet). */
  onCancel: () => void;
  /** Factor is on and any recovery codes were acknowledged. The caller must send the user to sign in. */
  onEnrolled: () => void;
  /** Signed-in only: fetch a fresh enrolment token after this one expires. */
  onRestart?: () => void;
  /**
   * Show a "two-step sign-in is on" step before onEnrolled. The signed-in path
   * passes false: enrolling rotates the session stamp, so the session is already
   * dead and the sign-in screen carries the same message.
   */
  confirmWhenDone?: boolean;
}

type SetupState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'failed'; reason: 'expired' | 'network' | 'server' };

/**
 * First-time TOTP enrolment on the same phone (a QR code cannot be scanned by
 * the device showing it): an otpauth:// deep link first, then a selectable
 * grouped setup key the user can long-press to copy or send with the system
 * share sheet (React Native core only, so this ships without a native build),
 * then the first code. The secret lives only in this component's state.
 */
export function MfaEnrollmentView({
  enrollmentToken,
  expiresInSeconds,
  tenantId,
  email,
  onCancel,
  onEnrolled,
  onRestart,
  confirmWhenDone = true,
}: MfaEnrollmentViewProps) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const styles = useMemo(() => makeStyles(theme), [theme]);
  const accent = theme.colors.accent;
  const [setup, setSetup] = useState<SetupState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [provisioningUri, setProvisioningUri] = useState('');
  const [secret, setSecret] = useState('');
  const [code, setCode] = useState('');
  const [noAuthenticator, setNoAuthenticator] = useState(false);
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [done, setDone] = useState(false);
  const { state, nowMs, run, edited } = useCodeEntry(expiresInSeconds);

  // The setup key is a long-lived credential: no screenshots or recordings
  // (Android FLAG_SECURE also blanks the recent-apps preview), and on iOS a blur
  // over the app-switcher snapshot while this screen is mounted.
  usePreventScreenCapture('mfa-enrollment');
  useEffect(() => {
    if (Platform.OS !== 'ios') return;
    void enableAppSwitcherProtectionAsync(1).catch(() => undefined);
    return () => {
      void disableAppSwitcherProtectionAsync().catch(() => undefined);
    };
  }, []);

  useEffect(() => {
    let active = true;
    setSetup({ kind: 'loading' });
    authApi
      .startMfaEnrollment(enrollmentToken, tenantId)
      .then((result) => {
        if (!active) return;
        setProvisioningUri(result.provisioningUri);
        setSecret(result.tempSecret);
        setSetup({ kind: 'ready' });
      })
      .catch((error: unknown) => {
        if (!active) return;
        const failure = classifyMfaFailure(error);
        setSetup({
          kind: 'failed',
          reason: failure === 'rejected' ? 'expired' : failure === 'network' ? 'network' : 'server',
        });
      });
    return () => {
      active = false;
    };
  }, [enrollmentToken, tenantId, attempt]);

  // Never keep the key past this screen.
  useEffect(
    () => () => {
      setSecret('');
      setProvisioningUri('');
      setRecoveryCodes(null);
    },
    []
  );

  // The user picks the destination (authenticator, password manager). A
  // dismissed or failed share sheet changes nothing.
  const share = (text: string) => {
    void Share.share({ message: text }).catch(() => undefined);
  };

  const openAuthenticator = async () => {
    if (!secret) return;
    try {
      await Linking.openURL(authenticatorUri(provisioningUri, secret, email));
      setNoAuthenticator(false);
    } catch {
      setNoAuthenticator(true);
    }
  };

  const canSubmit = state.phase === 'ready' && code.length === MFA_CODE_LENGTH && !!secret;
  const submitting = state.phase === 'submitting';

  const verify = () => {
    if (!canSubmit) return;
    const entered = code;
    setCode('');
    void run(async () => {
      const result = await authApi.verifyMfaEnrollment(enrollmentToken, secret, entered, tenantId);
      setSecret('');
      setProvisioningUri('');
      if (!result.recoveryCodes && !confirmWhenDone) {
        onEnrolled();
        return;
      }
      setRecoveryCodes(result.recoveryCodes);
      setDone(true);
    });
  };

  const expired = state.phase === 'expired' || (setup.kind === 'failed' && setup.reason === 'expired');
  const locked = state.phase === 'locked';

  const body = () => {
    if (done && recoveryCodes) {
      return (
        <>
          <Text style={styles.title}>{t('mfa.recoveryTitle')}</Text>
          <Text style={styles.subtitle}>{t('mfa.recoveryBody')}</Text>
          <View style={styles.secretBox}>
            {recoveryCodes.map((item) => (
              <Text key={item} selectable style={styles.recoveryCode}>{item}</Text>
            ))}
          </View>
          <TouchableOpacity style={styles.outlineButton} onPress={() => share(recoveryCodes.join('\n'))} accessibilityRole="button">
            <Ionicons accessible={false} name="share-outline" size={18} color={accent} />
            <Text style={styles.outlineButtonText}>{t('mfa.shareCodes')}</Text>
          </TouchableOpacity>
          <TouchableOpacity
            style={styles.primaryButton}
            onPress={() => (confirmWhenDone ? setRecoveryCodes(null) : onEnrolled())}
            accessibilityRole="button"
          >
            <Text style={styles.primaryText}>{t('mfa.savedCodes')}</Text>
          </TouchableOpacity>
        </>
      );
    }
    if (done) {
      return (
        <>
          <Text style={styles.title}>{t('mfa.doneTitle')}</Text>
          <Text style={styles.subtitle}>{t('mfa.doneSignInAgain')}</Text>
          <TouchableOpacity style={styles.primaryButton} onPress={onEnrolled} accessibilityRole="button">
            <Text style={styles.primaryText}>{t('mfa.continue')}</Text>
          </TouchableOpacity>
        </>
      );
    }

    const header = (
      <>
        <Text style={styles.title}>{t('mfa.enrolTitle')}</Text>
        <Text style={styles.subtitle}>{t('mfa.enrolRequiredBody')}</Text>
        <Text style={styles.account}>{email}</Text>
      </>
    );

    if (expired || locked) {
      const message = locked
        ? t('mfa.errors.attemptLimit')
        : onRestart ? t('mfa.setupExpired') : t('mfa.setupExpiredSignIn');
      return (
        <>
          {header}
          <View style={styles.errorBox}>
            <Text style={styles.errorBoxText}>{message}</Text>
          </View>
          {onRestart ? (
            <TouchableOpacity style={styles.primaryButton} onPress={onRestart} accessibilityRole="button">
              <Text style={styles.primaryText}>{t('mfa.startAgain')}</Text>
            </TouchableOpacity>
          ) : (
            <TouchableOpacity style={styles.primaryButton} onPress={onCancel} accessibilityRole="button">
              <Text style={styles.primaryText}>{t('mfa.backToSignIn')}</Text>
            </TouchableOpacity>
          )}
        </>
      );
    }

    if (setup.kind === 'loading') {
      return (
        <>
          {header}
          <ActivityIndicator color={accent} style={styles.loading} />
          <Text style={styles.helper}>{t('mfa.loadingSetup')}</Text>
        </>
      );
    }

    if (setup.kind === 'failed') {
      return (
        <>
          {header}
          <View style={styles.errorBox}>
            <Text style={styles.errorBoxText}>{t(`mfa.errors.${setup.reason}`)}</Text>
          </View>
          <TouchableOpacity style={styles.primaryButton} onPress={() => setAttempt((n) => n + 1)} accessibilityRole="button">
            <Text style={styles.primaryText}>{t('mfa.retry')}</Text>
          </TouchableOpacity>
        </>
      );
    }

    return (
      <>
        {header}
        <Text style={styles.stepTitle}>{t('mfa.step1')}</Text>
        <TouchableOpacity style={styles.outlineButton} onPress={openAuthenticator} accessibilityRole="button">
          <Ionicons accessible={false} name="open-outline" size={18} color={accent} />
          <Text style={styles.outlineButtonText}>{t('mfa.openAuthenticator')}</Text>
        </TouchableOpacity>
        {noAuthenticator ? <Text style={styles.warning}>{t('mfa.noAuthenticator')}</Text> : null}

        <Text style={styles.helper}>{t('mfa.manualKeyHelp')}</Text>
        <Text style={styles.helper}>{t('mfa.replaceExisting')}</Text>
        <View style={styles.secretBox}>
          <Text
            selectable
            style={styles.secret}
            // Spelled out so VoiceOver/TalkBack read characters, not words.
            accessibilityLabel={`${t('mfa.setupKeyLabel')}: ${spellSecretForScreenReader(secret)}`}
          >
            {groupSecret(secret)}
          </Text>
        </View>
        <Text style={styles.privateNote}>{t('mfa.keyPrivate')}</Text>
        <Text style={styles.securityNote}>
          {t('mfa.longPressToCopy')} {t('mfa.clipboardSync')}
        </Text>
        <TouchableOpacity style={styles.copyButton} onPress={() => share(secret)} accessibilityRole="button">
          <Ionicons accessible={false} name="share-outline" size={16} color={accent} />
          <Text style={styles.copyText}>{t('mfa.shareKey')}</Text>
        </TouchableOpacity>

        <Text style={styles.stepTitle}>{t('mfa.step2')}</Text>
        <MfaCodeInput
          value={code}
          onChange={(value) => {
            edited();
            setCode(value);
          }}
          editable={!submitting}
          onSubmit={verify}
        />
        <MfaCodeError state={state} />
        <Text style={styles.timer}>
          {t('mfa.setupExpiresIn', { time: formatCountdown(secondsLeft(state, nowMs)) })}
        </Text>

        <TouchableOpacity
          style={[styles.primaryButton, !canSubmit && styles.disabled]}
          onPress={verify}
          disabled={!canSubmit}
          accessibilityRole="button"
          accessibilityState={{ disabled: !canSubmit, busy: submitting }}
        >
          {submitting ? <ActivityIndicator color={theme.colors.onFill} /> : <Text style={styles.primaryText}>{t('mfa.enable')}</Text>}
        </TouchableOpacity>
      </>
    );
  };

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : 'height'}>
      <LiquidBackdrop subtle />
      {!done ? (
        <TouchableOpacity
          style={styles.back}
          onPress={onCancel}
          accessibilityRole="button"
          accessibilityLabel={onRestart ? t('mfa.cancel') : t('mfa.backToSignIn')}
        >
          <DirectionalIcon name="back" size={22} color={accent} />
        </TouchableOpacity>
      ) : null}

      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
        <View style={styles.card}>
          <View style={styles.iconCircle}>
            <Ionicons accessible={false} name={done ? 'shield-checkmark-outline' : 'key-outline'} size={34} color={accent} />
          </View>
          {body()}
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function makeStyles(theme: KynexTheme) {
  const c = theme.colors;
  return StyleSheet.create({
    container: { flex: 1, backgroundColor: c.canvas },
    scroll: { flexGrow: 1, justifyContent: 'center', padding: 24, paddingTop: 90, paddingBottom: 50 },
    back: {
      position: 'absolute', top: 54, start: 16, zIndex: 2,
      width: 44, height: 44, alignItems: 'center', justifyContent: 'center',
    },
    card: {
      borderRadius: theme.radius.lg,
      padding: 24,
      backgroundColor: c.cardRaised,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: c.hairline,
      ...theme.shadows.raised,
    },
    iconCircle: {
      width: 64,
      height: 64,
      borderRadius: 32,
      alignSelf: 'center',
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: c.surfaceInset,
      marginBottom: 16,
    },
    title: { ...theme.typography.title2, color: c.text, textAlign: 'center' },
    subtitle: { ...theme.typography.subheadline, color: c.textSecondary, textAlign: 'center', marginTop: 8 },
    account: { ...theme.typography.footnote, color: c.accent, textAlign: 'center', fontWeight: '600', marginTop: 7 },
    loading: { marginTop: 28, marginBottom: 10 },
    stepTitle: { ...theme.typography.subheadline, color: c.text, fontWeight: '700', marginTop: 24, marginBottom: 10, textAlign: 'auto' },
    outlineButton: {
      flexDirection: 'row',
      justifyContent: 'center',
      alignItems: 'center',
      gap: 8,
      backgroundColor: c.surfaceInset,
      borderRadius: theme.radius.sm,
      minHeight: 48,
      paddingVertical: 12,
      marginTop: 4,
    },
    outlineButtonText: { ...theme.typography.callout, color: c.accent, fontWeight: '700' },
    warning: { ...theme.typography.footnote, color: c.status.warning.fg, marginTop: 10, textAlign: 'auto' },
    helper: { ...theme.typography.footnote, color: c.textSecondary, marginTop: 14, textAlign: 'auto' },
    secretBox: { backgroundColor: c.surfaceInset, borderRadius: 10, padding: 13, marginTop: 9 },
    // Setup keys and recovery codes are Latin/digits: keep them left-to-right in Arabic.
    secret: { color: c.text, textAlign: 'center', fontSize: 17, fontWeight: '800', letterSpacing: 2, writingDirection: 'ltr' },
    recoveryCode: { color: c.text, textAlign: 'center', fontSize: 15, fontWeight: '700', letterSpacing: 1, writingDirection: 'ltr', paddingVertical: 2 },
    copyButton: { flexDirection: 'row', alignSelf: 'center', alignItems: 'center', gap: 6, minHeight: 44, paddingHorizontal: 14 },
    copyText: { ...theme.typography.footnote, color: c.accent, fontWeight: '700' },
    privateNote: { ...theme.typography.footnote, color: c.status.warning.fg, textAlign: 'center', marginTop: 10 },
    securityNote: { ...theme.typography.caption, color: c.textMuted, textAlign: 'center', marginTop: 6 },
    timer: { ...theme.typography.footnote, color: c.textMuted, textAlign: 'center', marginTop: 12 },
    primaryButton: {
      backgroundColor: c.primaryFill,
      borderRadius: theme.radius.md,
      alignItems: 'center',
      justifyContent: 'center',
      minHeight: 52,
      paddingVertical: 14,
      marginTop: 20,
    },
    primaryText: { ...theme.typography.headline, color: c.onFill },
    disabled: { opacity: 0.45 },
    errorBox: { backgroundColor: c.status.danger.tint, borderRadius: theme.radius.sm, padding: 14, marginTop: 22 },
    errorBoxText: { ...theme.typography.footnote, color: c.text, textAlign: 'center' },
  });
}
