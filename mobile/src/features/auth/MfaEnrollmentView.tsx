import React, { useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  I18nManager,
  KeyboardAvoidingView,
  Linking,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import * as Clipboard from 'expo-clipboard';
import { Ionicons } from '@expo/vector-icons';
import { LinearGradient } from 'expo-linear-gradient';
import { useTranslation } from 'react-i18next';
import { authApi } from '@/api/services';
import {
  authenticatorUri,
  classifyMfaFailure,
  formatCountdown,
  groupSecret,
  MFA_CODE_LENGTH,
  secondsLeft,
} from '@/auth/mfaFlow';
import { COLORS } from '@/config';
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
 * the device showing it): grouped setup key with Copy, an otpauth:// deep link,
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
  const [setup, setSetup] = useState<SetupState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [provisioningUri, setProvisioningUri] = useState('');
  const [secret, setSecret] = useState('');
  const [code, setCode] = useState('');
  const [copied, setCopied] = useState(false);
  const [noAuthenticator, setNoAuthenticator] = useState(false);
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [done, setDone] = useState(false);
  const copiedToClipboard = useRef(false);
  const { state, nowMs, run, edited } = useCodeEntry(expiresInSeconds);

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

  // Never keep the key past this screen: drop it from state, and take it back
  // off the clipboard if we put it there.
  useEffect(
    () => () => {
      setSecret('');
      setProvisioningUri('');
      setRecoveryCodes(null);
      if (copiedToClipboard.current) void Clipboard.setStringAsync('').catch(() => undefined);
    },
    []
  );

  const copy = async (text: string) => {
    try {
      await Clipboard.setStringAsync(text);
      copiedToClipboard.current = true;
      setCopied(true);
    } catch {
      setCopied(false);
    }
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
      setCopied(false);
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
          <TouchableOpacity style={styles.outlineButton} onPress={() => void copy(recoveryCodes.join('\n'))} accessibilityRole="button">
            <Ionicons name="copy-outline" size={18} color={COLORS.blue} />
            <Text style={styles.outlineButtonText}>{copied ? t('mfa.copied') : t('mfa.copyCodes')}</Text>
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
          <ActivityIndicator color={COLORS.cyan} style={styles.loading} />
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
          <Ionicons name="open-outline" size={18} color={COLORS.blue} />
          <Text style={styles.outlineButtonText}>{t('mfa.openAuthenticator')}</Text>
        </TouchableOpacity>
        {noAuthenticator ? <Text style={styles.warning}>{t('mfa.noAuthenticator')}</Text> : null}

        <Text style={styles.helper}>{t('mfa.manualKeyHelp')}</Text>
        <View style={styles.secretBox} accessible accessibilityLabel={t('mfa.setupKeyLabel')}>
          <Text selectable style={styles.secret}>{groupSecret(secret)}</Text>
        </View>
        <TouchableOpacity style={styles.copyButton} onPress={() => void copy(secret)} accessibilityRole="button">
          <Ionicons name={copied ? 'checkmark' : 'copy-outline'} size={16} color={COLORS.cyan} />
          <Text style={styles.copyText}>{copied ? t('mfa.copied') : t('mfa.copyKey')}</Text>
        </TouchableOpacity>
        <Text style={styles.securityNote}>{t('mfa.keyPrivate')}</Text>

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
          {submitting ? <ActivityIndicator color="#fff" /> : <Text style={styles.primaryText}>{t('mfa.enable')}</Text>}
        </TouchableOpacity>
      </>
    );
  };

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : 'height'}>
      <LinearGradient colors={['#0B1020', '#0F1830', '#0B1020']} style={StyleSheet.absoluteFill} />
      {!done ? (
        <TouchableOpacity
          style={styles.back}
          onPress={onCancel}
          accessibilityRole="button"
          accessibilityLabel={onRestart ? t('mfa.cancel') : t('mfa.backToSignIn')}
        >
          <Ionicons name={I18nManager.isRTL ? 'arrow-forward' : 'arrow-back'} size={22} color="#fff" />
        </TouchableOpacity>
      ) : null}

      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
        <View style={styles.card}>
          <View style={styles.iconCircle}>
            <Ionicons name={done ? 'shield-checkmark-outline' : 'key-outline'} size={34} color={COLORS.cyan} />
          </View>
          {body()}
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.navy },
  scroll: { flexGrow: 1, justifyContent: 'center', padding: 24, paddingTop: 90, paddingBottom: 50 },
  back: { position: 'absolute', top: 58, start: 22, zIndex: 2, padding: 8 },
  card: {
    borderRadius: 22,
    padding: 24,
    backgroundColor: 'rgba(255,255,255,0.055)',
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.1)',
  },
  iconCircle: {
    width: 64,
    height: 64,
    borderRadius: 32,
    alignSelf: 'center',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(94,235,255,0.1)',
    marginBottom: 16,
  },
  title: { color: '#fff', fontSize: 22, fontWeight: '800', textAlign: 'center' },
  subtitle: { color: 'rgba(255,255,255,0.62)', fontSize: 14, lineHeight: 20, textAlign: 'center', marginTop: 8 },
  account: { color: COLORS.cyan, textAlign: 'center', fontSize: 13, fontWeight: '600', marginTop: 7 },
  loading: { marginTop: 28, marginBottom: 10 },
  stepTitle: { color: '#fff', fontSize: 14, fontWeight: '700', marginTop: 24, marginBottom: 10, textAlign: 'auto' },
  outlineButton: {
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
    gap: 8,
    backgroundColor: '#fff',
    borderRadius: 12,
    paddingVertical: 13,
    marginTop: 4,
  },
  outlineButtonText: { color: COLORS.blue, fontWeight: '700' },
  warning: { color: '#FCD34D', fontSize: 12, lineHeight: 18, marginTop: 10, textAlign: 'auto' },
  helper: { color: 'rgba(255,255,255,0.55)', fontSize: 12, lineHeight: 18, marginTop: 14, textAlign: 'auto' },
  secretBox: { backgroundColor: 'rgba(255,255,255,0.08)', borderRadius: 10, padding: 13, marginTop: 9 },
  // Setup keys and recovery codes are Latin/digits: keep them left-to-right in Arabic.
  secret: { color: '#fff', textAlign: 'center', fontSize: 17, fontWeight: '800', letterSpacing: 2, writingDirection: 'ltr' },
  recoveryCode: { color: '#fff', textAlign: 'center', fontSize: 15, fontWeight: '700', letterSpacing: 1, writingDirection: 'ltr', paddingVertical: 2 },
  copyButton: { flexDirection: 'row', alignSelf: 'center', alignItems: 'center', gap: 6, paddingVertical: 10, paddingHorizontal: 14 },
  copyText: { color: COLORS.cyan, fontSize: 13, fontWeight: '700' },
  securityNote: { color: 'rgba(255,255,255,0.4)', fontSize: 11, lineHeight: 16, textAlign: 'center' },
  timer: { color: 'rgba(255,255,255,0.45)', textAlign: 'center', marginTop: 12, fontSize: 12 },
  primaryButton: { backgroundColor: COLORS.blue, borderRadius: 14, alignItems: 'center', paddingVertical: 15, marginTop: 20 },
  primaryText: { color: '#fff', fontSize: 15, fontWeight: '800' },
  disabled: { opacity: 0.45 },
  errorBox: { backgroundColor: 'rgba(239,68,68,0.12)', borderRadius: 12, padding: 14, marginTop: 22 },
  errorBoxText: { color: '#FCA5A5', textAlign: 'center', fontSize: 13, lineHeight: 19 },
});
