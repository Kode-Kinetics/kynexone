import React, { useEffect, useRef, useState } from 'react';
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';
import { authApi } from '@/api/services';
import { authFailure, useAuthStore } from '@/auth/authStore';
import { normalizeEmail, normalizeWorkspace } from '@/auth/publicAuthInput';
import {
  DEFAULT_MIN_PASSWORD_LENGTH,
  formatWelcomeCode,
  hasArabicLetters,
  isAmbiguousFailure,
  isWelcomeCode,
  normalizeWelcomeCode,
  passwordChecks,
  welcomeErrorKey,
} from '@/auth/welcomeCode';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { SignInLanguageToggle } from './SignInLanguageToggle';

type Props = NativeStackScreenProps<AuthStackParamList, 'Welcome'>;
type Step = 'code' | 'password';

/** Unicode isolates: the email reads left-to-right inside Arabic text. */
const LRI = String.fromCharCode(0x2066);
const PDI = String.fromCharCode(0x2069);

/**
 * First sign-in with the 8-digit welcome code HR gave the employee (same flow as web /welcome).
 *
 * Arrives with the email and code already known (the sign-in screen sent a code typed as a password
 * here, as in-memory navigation params) or with nothing: then work email + code, Continue, and the
 * password step. Saving redeems the code (no session), then signs in with the new password; the root
 * navigator moves to the app as soon as the session exists. Refusals are mapped from their code to
 * the copy deck's sentences; server text is never shown. The code lives in this screen's state only.
 *
 * Deep links: the slip's QR is an https:// URL for the web /welcome. The app has no linking config
 * and no associated domains (universal / app links), so the QR opens the browser, not this screen.
 */
export default function WelcomeScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const login = useAuthStore((s) => s.login);
  const clearError = useAuthStore((s) => s.clearError);

  const [step, setStep] = useState<Step>('code');
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(true);
  const [workspace, setWorkspace] = useState('');
  const [showWorkspace, setShowWorkspace] = useState(false);
  const [error, setError] = useState('');
  const [info, setInfo] = useState('');
  const [busy, setBusy] = useState(false);
  const [passwordSet, setPasswordSet] = useState(false);
  const [minLength, setMinLength] = useState(DEFAULT_MIN_PASSWORD_LENGTH);
  const inFlight = useRef(false);
  const consumed = useRef(false);

  useEffect(() => {
    if (consumed.current) return;
    consumed.current = true;
    const params = route.params ?? {};
    const givenEmail = normalizeEmail(params.email ?? '');
    const givenCode = normalizeWelcomeCode(params.code);
    if (givenEmail) setEmail(givenEmail);
    if (params.workspace) setWorkspace(normalizeWorkspace(params.workspace));
    if (givenCode) setCode(givenCode);
    if (givenEmail && isWelcomeCode(givenCode)) {
      setStep('password');
      if (params.fromLogin) setInfo(t('signin.lookedLikeCode'));
    }
    // Do not keep the code in the navigation state any longer than it takes to read it.
    navigation.setParams({ code: undefined, fromLogin: undefined });
  }, [navigation, route.params, t]);

  const policySlug = normalizeWorkspace(workspace);
  useEffect(() => {
    let live = true;
    void authApi.passwordPolicy(policySlug || undefined).then((n) => { if (live) setMinLength(n); });
    return () => { live = false; };
  }, [policySlug]);

  const checks = passwordChecks(password, email, minLength);
  const arabicLetters = hasArabicLetters(password);
  const workspaceArg = () => normalizeWorkspace(workspace) || undefined;

  const notMyEmail = () => {
    setEmail(''); setCode(''); setPassword('');
    setError(''); setInfo('');
    setStep('code');
  };

  const onContinue = () => {
    setError(''); setInfo('');
    if (!email.trim() || !email.includes('@')) return setError(t('signin.emailRequired'));
    if (!isWelcomeCode(code)) return setError(t('signin.codeShape'));
    if (showWorkspace && !workspace.trim()) return setError(t('signin.companyIdRequired'));
    setCode(normalizeWelcomeCode(code));
    setStep('password');
  };

  /** Sign in with the saved password. On success the root navigator switches to the app. */
  const signIn = async (companyId?: string): Promise<boolean> => {
    try {
      const outcome = await login(email, password, companyId ?? workspaceArg());
      if (outcome.kind === 'mfaChallenge') navigation.navigate('MfaChallenge', outcome);
      else if (outcome.kind === 'mfaEnrollment') navigation.navigate('MfaEnrollment', outcome);
      return true;
    } catch {
      // The store raised its own alert for this failure; this screen says what to do next instead.
      clearError();
      return false;
    }
  };

  const onSave = async () => {
    if (inFlight.current) return;
    setError(''); setInfo('');
    if (!checks.longEnough || !checks.notPersonal) return setError(t('signin.errors.passwordRules'));
    if (showWorkspace && !workspace.trim()) return setError(t('signin.companyIdRequired'));
    inFlight.current = true;
    setBusy(true);
    try {
      let companyId = workspaceArg();
      if (!passwordSet) {
        const redeemed = await authApi.welcomeRedeem(email, code, password, workspaceArg());
        setCode('');
        setPasswordSet(true);
        if (redeemed.tenantSlug) { companyId = redeemed.tenantSlug; setWorkspace(redeemed.tenantSlug); }
      }
      if (!(await signIn(companyId))) setInfo(t('signin.passwordSaved'));
    } catch (failure) {
      const { status, code: reason } = authFailure(failure);
      if (isAmbiguousFailure(status)) {
        // The single-use code may have been spent before the reply was lost: settle it by signing in.
        if (!(await signIn())) setError(t(status === undefined ? 'signin.errors.network' : 'signin.errors.generic'));
        return;
      }
      const key = welcomeErrorKey(status, reason);
      if (key === 'codeInvalid') {
        setStep('code');
        setCode(formatWelcomeCode(code));
      }
      if (key === 'companyIdNeeded') setShowWorkspace(true);
      setError(t(`signin.errors.${key}`));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  };

  const ltrInput = { textAlign: 'left' as const };

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop />
      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('signin.kicker')}
          title={t('signin.title')}
          subtitle={step === 'code' ? t('signin.intro') : undefined}
          onBack={() => navigation.goBack()}
          backLabel={t('mfa.backToSignIn')}
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            {step === 'code' ? (
              <>
                <GlassTextField
                  label={t('signin.workEmail')}
                  icon="mail-outline"
                  value={email}
                  onChangeText={setEmail}
                  placeholder={t('signin.emailPlaceholder')}
                  autoCapitalize="none"
                  autoCorrect={false}
                  keyboardType="email-address"
                  autoComplete="username"
                  textContentType="username"
                  style={ltrInput}
                  testID="welcome-email-input"
                />
                <GlassTextField
                  label={t('signin.code')}
                  icon="keypad-outline"
                  value={code}
                  onChangeText={setCode}
                  placeholder="1234 5678"
                  keyboardType="number-pad"
                  autoComplete="one-time-code"
                  textContentType="oneTimeCode"
                  maxLength={16}
                  style={ltrInput}
                  testID="welcome-code-input"
                />
                <Text style={[theme.typography.micro, styles.hint, { color: theme.colors.textMuted }]}>{t('signin.codeHint')}</Text>
              </>
            ) : (
              <>
                <View style={[styles.who, { borderColor: theme.colors.border }]}>
                  {/* The email is a left-to-right run in Arabic too. */}
                  <Text style={[theme.typography.bodyStrong, styles.whoEmail, { color: theme.colors.text }]}
                    numberOfLines={2} testID="welcome-email">
                    {LRI}{email}{PDI}
                  </Text>
                  <MotionPressable accessibilityRole="button" onPress={notMyEmail} haptic="selection"
                    contentStyle={styles.linkButton} testID="welcome-not-me">
                    <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
                      {t('signin.notYourEmail')}
                    </Text>
                  </MotionPressable>
                </View>
                <GlassTextField
                  label={t('signin.choosePassword')}
                  icon="lock-closed-outline"
                  value={password}
                  onChangeText={setPassword}
                  autoCapitalize="none"
                  autoCorrect={false}
                  spellCheck={false}
                  autoComplete="new-password"
                  textContentType="newPassword"
                  secureTextEntry={!showPassword}
                  autoFocus
                  style={ltrInput}
                  testID="welcome-password-input"
                  trailing={
                    <MotionPressable
                      accessibilityRole="button"
                      accessibilityLabel={showPassword ? t('signin.hidePassword') : t('signin.showPassword')}
                      onPress={() => setShowPassword((shown) => !shown)}
                      haptic="selection"
                      contentStyle={styles.reveal}
                    >
                      <Ionicons accessible={false} name={showPassword ? 'eye-off-outline' : 'eye-outline'}
                        size={20} color={theme.colors.textMuted} />
                    </MotionPressable>
                  }
                />
                <View style={styles.checks} accessibilityLiveRegion="polite">
                  <Check ok={checks.longEnough} label={t('signin.atLeast', { n: minLength })} />
                  <Check ok={checks.notPersonal} label={t('signin.notPersonal')} />
                </View>
                {arabicLetters ? (
                  <Text style={[theme.typography.caption, styles.hint, { color: theme.colors.textSecondary }]}>
                    {t('signin.englishLetters')}
                  </Text>
                ) : null}
              </>
            )}

            {showWorkspace ? (
              <GlassTextField
                label={t('signin.companyId')}
                icon="business-outline"
                value={workspace}
                onChangeText={setWorkspace}
                placeholder="your-company"
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="organization"
                style={ltrInput}
              />
            ) : null}

            {error ? (
              <Text accessibilityRole="alert" style={[theme.typography.caption, styles.message, { color: theme.colors.danger }]}>
                {error}
              </Text>
            ) : info ? (
              <Text accessibilityLiveRegion="polite" style={[theme.typography.caption, styles.message, { color: theme.colors.text }]}>
                {info}
              </Text>
            ) : null}

            {step === 'code' ? (
              <LiquidButton label={t('signin.continue')} icon="arrow-forward-outline" onPress={onContinue} testID="welcome-continue" />
            ) : passwordSet && info ? (
              <LiquidButton label={t('auth.login')} icon="log-in-outline" onPress={() => navigation.navigate('Login', { email, tenantId: workspaceArg() })} />
            ) : (
              <LiquidButton
                label={busy ? t('signin.saving') : t('signin.save')}
                icon="checkmark-circle-outline"
                onPress={() => void onSave()}
                loading={busy}
                disabled={busy || !checks.longEnough || !checks.notPersonal}
                testID="welcome-save"
              />
            )}

            <Text style={[theme.typography.caption, styles.footer, { color: theme.colors.textSecondary }]}>
              {t('signin.forgotFooter')}
            </Text>
          </GlassSurface>
          <SignInLanguageToggle />
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function Check({ ok, label }: { ok: boolean; label: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.check} accessible accessibilityLabel={label} accessibilityState={{ checked: ok }}>
      <Ionicons accessible={false} name={ok ? 'checkmark-circle' : 'ellipse-outline'} size={16}
        color={ok ? theme.colors.success : theme.colors.textMuted} />
      <Text style={[theme.typography.caption, { color: ok ? theme.colors.text : theme.colors.textMuted, flex: 1 }]}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 32 },
  content: { paddingHorizontal: 16, gap: 12 },
  form: { padding: 20, gap: 12 },
  hint: { marginTop: -4, lineHeight: 18 },
  who: {
    borderWidth: 1, borderRadius: 14, paddingHorizontal: 12, paddingVertical: 10,
    flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 6,
  },
  whoEmail: { flexShrink: 1, writingDirection: 'ltr' },
  linkButton: { minHeight: 44, justifyContent: 'center', paddingHorizontal: 4 },
  reveal: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center', borderRadius: 14 },
  checks: { gap: 6 },
  check: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  message: { lineHeight: 18 },
  footer: { textAlign: 'center', marginTop: 4, lineHeight: 18 },
});
