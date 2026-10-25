import React, { useCallback, useEffect, useState } from 'react';
import {
  Image,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import Animated, {
  Easing,
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withDelay,
  withTiming,
} from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';
import { useAuthStore } from '@/auth/authStore';
import { appStorage } from '@/storage';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  Surface,
} from '@/components/ui';
import { normalizeEmail, normalizeWorkspace } from '@/auth/publicAuthInput';
import { authFailure } from '@/auth/authStore';
import { isWelcomeCode, normalizeWelcomeCode } from '@/auth/welcomeCode';
import { haptics } from '@/utils/haptics';
import { SignInLanguageToggle } from './SignInLanguageToggle';
import { loginErrorKey, type LoginErrorKey } from './loginError';
import { getServer } from '@/config/server';

/* The company ID is asked for only when needed (a link that names it, or the server answering
   `workspace_required`), so it is validated in onSubmit rather than by the schema. Messages are
   translation keys, resolved where they are shown. */
const loginSchema = z.object({
  tenantId: z.string(),
  username: z.string().refine((value) => value.trim().length > 0, 'signin.emailRequired'),
  password: z.string().min(1, 'signin.passwordRequired'),
});

type LoginFormData = z.infer<typeof loginSchema>;
type Props = NativeStackScreenProps<AuthStackParamList, 'Login'>;

/**
 * The client's first impression. Layout, top to bottom: language switch (glass chrome over the
 * brand image) · wordmark and one-line promise · a solid sign-in card (heading, inline error,
 * email, password, optional company ID, forgot-password, Sign in) · the welcome-code way in.
 * Motion: one short entrance (fade + 8–12pt rise, ≤260ms), nothing looping, no 3D.
 */
export default function LoginScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { theme, reduceMotion } = useTheme();
  const insets = useSafeAreaInsets();
  const { width, height, fontScale } = useWindowDimensions();
  const compact = height < 760 || fontScale > 1.2;
  const narrow = width < 370;
  const { login, isLoading, clearError } = useAuthStore();
  const mfaEnrolledNotice = useAuthStore((s) => s.mfaEnrolledNotice);
  const consumeMfaEnrolledNotice = useAuthStore((s) => s.consumeMfaEnrolledNotice);
  const [mfaJustEnabled, setMfaJustEnabled] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [loginSucceeded, setLoginSucceeded] = useState(false);
  const [showWorkspace, setShowWorkspace] = useState(!!route.params?.tenantId);
  const [companyIdNeeded, setCompanyIdNeeded] = useState(false);
  const [failure, setFailure] = useState<LoginErrorKey | null>(null);
  const entrance = useSharedValue(reduceMotion ? 1 : 0);

  const {
    control,
    handleSubmit,
    setValue,
    getValues,
    setError,
    formState: { errors },
  } = useForm<LoginFormData>({
    resolver: zodResolver(loginSchema),
    defaultValues: { tenantId: '', username: '', password: '' },
  });

  // A remembered company ID is only a pre-fill for the day the server asks for it; it is not sent
  // unless the field is showing.
  const loadRememberedTenant = useCallback(async () => {
    if (route.params?.tenantId) return;
    const tenant = await appStorage.get<string>('zayra_tenant_id');
    if (tenant) setValue('tenantId', normalizeWorkspace(tenant));
  }, [route.params?.tenantId, setValue]);

  useEffect(() => {
    void loadRememberedTenant();
  }, [loadRememberedTenant]);

  useEffect(() => {
    if (route?.params?.tenantId) {
      setValue('tenantId', normalizeWorkspace(route.params.tenantId));
      setShowWorkspace(true);
    }
    if (route?.params?.email) setValue('username', normalizeEmail(route.params.email));
    if (route?.params?.enrollmentComplete) setMfaJustEnabled(true);
  }, [route?.params, setValue]);

  // Signed-in enrolment ends the session; say why and keep the account filled in.
  useEffect(() => {
    if (!mfaEnrolledNotice) return;
    if (mfaEnrolledNotice.tenantId) {
      setValue('tenantId', normalizeWorkspace(mfaEnrolledNotice.tenantId));
      setShowWorkspace(true);
    }
    setValue('username', normalizeEmail(mfaEnrolledNotice.email));
    setMfaJustEnabled(true);
    consumeMfaEnrolledNotice();
  }, [consumeMfaEnrolledNotice, mfaEnrolledNotice, setValue]);

  useEffect(() => {
    entrance.value = reduceMotion
      ? 1
      : withDelay(60, withTiming(1, { duration: 260, easing: Easing.out(Easing.cubic) }));
  }, [entrance, reduceMotion]);

  const brandMotion = useAnimatedStyle(() => ({
    opacity: entrance.value,
    transform: [{ translateY: interpolate(entrance.value, [0, 1], [8, 0]) }],
  }));
  const cardMotion = useAnimatedStyle(() => ({
    opacity: interpolate(entrance.value, [0, 0.3, 1], [0, 0, 1]),
    transform: [{ translateY: interpolate(entrance.value, [0, 1], [12, 0]) }],
  }));

  const onSubmit = useCallback(
    async (data: LoginFormData) => {
      setFailure(null);
      const workspace = showWorkspace ? normalizeWorkspace(data.tenantId) : '';
      if (showWorkspace && !workspace) {
        setError('tenantId', { message: 'signin.companyIdRequired' });
        return;
      }
      try {
        const outcome = await login(normalizeEmail(data.username), data.password, workspace || undefined);
        if (outcome.kind === 'mfaChallenge') {
          navigation.navigate('MfaChallenge', { ...outcome, justEnrolled: mfaJustEnabled });
        } else if (outcome.kind === 'mfaEnrollment') {
          navigation.navigate('MfaEnrollment', outcome);
        } else {
          haptics.success();
          setLoginSucceeded(true);
          // A live reset code is reported by ResetCodeBanner in the signed-in shell (Amendment 3, F1).
        }
      } catch (error) {
        // The store keeps the server's raw English message; this screen shows its own plain words.
        clearError();
        const { status, code } = authFailure(error);
        if (status === 400 && code === 'workspace_required') {
          setShowWorkspace(true);
          setCompanyIdNeeded(true);
          return;
        }
        // The 8-digit welcome code typed as the password: only after the sign-in failed, and the
        // email and code travel as in-memory navigation params (no URL, no storage).
        if ((status === 401 || status === 400) && isWelcomeCode(data.password)) {
          setValue('password', '');
          navigation.navigate('Welcome', {
            email: normalizeEmail(data.username),
            code: normalizeWelcomeCode(data.password),
            workspace: workspace || undefined,
            fromLogin: true,
          });
          return;
        }
        haptics.error();
        setFailure(loginErrorKey(error));
      }
    },
    [clearError, login, mfaJustEnabled, navigation, setError, setValue, showWorkspace],
  );

  const openWelcome = useCallback(() => {
    const email = normalizeEmail(getValues('username'));
    const workspace = showWorkspace ? normalizeWorkspace(getValues('tenantId')) : '';
    navigation.navigate('Welcome', { email: email || undefined, workspace: workspace || undefined });
  }, [getValues, navigation, showWorkspace]);

  const info = theme.colors.status.info;
  const danger = theme.colors.status.danger;

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop />
      {/* Brand image: static, decorative, anchored to the reading-end corner. */}
      <View pointerEvents="none" style={[styles.heroImage, compact && styles.heroImageCompact]}>
        <Image
          accessible={false}
          source={require('../../../assets/login-cinematic-orb.png')}
          resizeMode="cover"
          style={styles.fill}
        />
      </View>

      <ScrollView
        style={styles.foreground}
        contentContainerStyle={[
          styles.scroll,
          {
            paddingTop: insets.top + 8,
            paddingBottom: Math.max(insets.bottom + 16, 24),
            paddingHorizontal: narrow ? 14 : 20,
          },
        ]}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.topBar}>
          {/* Chrome: the only glass on this screen — a floating control over the brand image. */}
          <GlassSurface glass radius={theme.radius.pill} contentStyle={styles.languageChrome}>
            <SignInLanguageToggle />
          </GlassSurface>
        </View>

        <Animated.View style={[styles.brandBlock, compact && styles.brandBlockCompact, brandMotion]}>
          <Text
            accessibilityRole="header"
            accessibilityLabel="KynexOne"
            style={[styles.wordmark, { color: theme.colors.text }]}
          >
            KYNEXONE
          </Text>
          <Text style={[theme.typography.title3, styles.tagline, { color: theme.colors.text }]}>
            {t('account.login.tagline')}
          </Text>
          <Text style={[theme.typography.subheadline, styles.taglineSub, { color: theme.colors.textSecondary }]}>
            {t('account.login.taglineSub')}
          </Text>
        </Animated.View>

        <Animated.View style={[styles.cardWrap, cardMotion]}>
          <Surface level={2} radius={theme.radius.xl} contentStyle={[styles.card, compact && styles.cardCompact]}>
            <Text accessibilityRole="header" style={[theme.typography.title2, { color: theme.colors.text }]}>
              {t('account.login.title')}
            </Text>
            <Text style={[theme.typography.subheadline, styles.cardSub, { color: theme.colors.textSecondary }]}>
              {t('account.login.subtitle')}
            </Text>

            {mfaJustEnabled ? (
              <View accessibilityRole="alert" style={[styles.banner, { backgroundColor: info.tint }]}>
                <Ionicons accessible={false} name="shield-checkmark-outline" size={20} color={info.fg} />
                <View style={styles.flex}>
                  <Text style={[theme.typography.subheadline, styles.bannerTitle, { color: theme.colors.text }]}>
                    {t('mfa.doneTitle')}
                  </Text>
                  <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                    {t('mfa.doneSignInAgain')}
                  </Text>
                </View>
              </View>
            ) : null}

            {failure ? (
              <View
                accessibilityRole="alert"
                accessibilityLiveRegion="assertive"
                style={[styles.banner, { backgroundColor: danger.tint }]}
                testID="login-error"
              >
                <Ionicons accessible={false} name="alert-circle-outline" size={20} color={danger.fg} />
                <Text style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>
                  {t(`account.login.errors.${failure}`)}
                </Text>
              </View>
            ) : null}

            <Controller
              control={control}
              name="username"
              render={({ field: { onChange, value, onBlur } }) => (
                <GlassTextField
                  label={t('signin.workEmail')}
                  icon="mail-outline"
                  value={value}
                  onChangeText={(next) => {
                    onChange(next);
                    if (failure) setFailure(null);
                  }}
                  onBlur={onBlur}
                  placeholder={t('signin.emailPlaceholder')}
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="username"
                  textContentType="username"
                  keyboardType="email-address"
                  returnKeyType="next"
                  error={errors.username?.message ? t(errors.username.message as 'signin.emailRequired') : undefined}
                />
              )}
            />
            <Controller
              control={control}
              name="password"
              render={({ field: { onChange, value, onBlur } }) => (
                <GlassTextField
                  label={t('signin.password')}
                  icon="lock-closed-outline"
                  value={value}
                  onChangeText={(next) => {
                    onChange(next);
                    if (failure) setFailure(null);
                  }}
                  onBlur={onBlur}
                  placeholder={t('signin.passwordPlaceholder')}
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="current-password"
                  textContentType="password"
                  spellCheck={false}
                  secureTextEntry={!showPassword}
                  returnKeyType="done"
                  onSubmitEditing={handleSubmit(onSubmit)}
                  error={errors.password?.message ? t(errors.password.message as 'signin.passwordRequired') : undefined}
                  trailing={
                    <MotionPressable
                      accessibilityRole="button"
                      accessibilityLabel={showPassword ? t('signin.hidePassword') : t('signin.showPassword')}
                      onPress={() => setShowPassword((current) => !current)}
                      haptic="selection"
                      contentStyle={styles.iconButton}
                    >
                      <Ionicons
                        accessible={false}
                        name={showPassword ? 'eye-off-outline' : 'eye-outline'}
                        size={20}
                        color={theme.colors.textMuted}
                      />
                    </MotionPressable>
                  }
                />
              )}
            />

            {showWorkspace ? (
              <>
                {companyIdNeeded ? (
                  <Text accessibilityRole="alert" style={[theme.typography.footnote, styles.companyNotice, { color: theme.colors.text }]}>
                    {t('signin.companyIdNeeded')}
                  </Text>
                ) : null}
                <Controller
                  control={control}
                  name="tenantId"
                  render={({ field: { onChange, value, onBlur } }) => (
                    <GlassTextField
                      label={t('signin.companyId')}
                      icon="business-outline"
                      value={value}
                      onChangeText={onChange}
                      onBlur={onBlur}
                      placeholder={t('account.login.companyIdPlaceholder')}
                      autoCapitalize="none"
                      autoCorrect={false}
                      autoComplete="organization"
                      autoFocus={companyIdNeeded}
                      returnKeyType="done"
                      onSubmitEditing={handleSubmit(onSubmit)}
                      error={errors.tenantId?.message ? t(errors.tenantId.message as 'signin.companyIdRequired') : undefined}
                    />
                  )}
                />
              </>
            ) : null}

            <MotionPressable
              accessibilityRole="button"
              accessibilityLabel={t('auth.forgotPassword')}
              onPress={() => navigation.navigate('ForgotPassword')}
              hitSlop={4}
              style={styles.forgotShell}
              contentStyle={styles.forgot}
            >
              <Text style={[theme.typography.subheadline, styles.link, { color: theme.colors.accent }]}>
                {t('auth.forgotPassword')}
              </Text>
            </MotionPressable>

            <LiquidButton
              label={loginSucceeded ? t('signin.signedInTitle') : t('auth.login')}
              icon={loginSucceeded ? 'checkmark-circle-outline' : undefined}
              onPress={handleSubmit(onSubmit)}
              loading={isLoading}
              disabled={isLoading || loginSucceeded}
              variant={loginSucceeded ? 'success' : 'primary'}
              accessibilityHint={isLoading ? t('account.login.signingIn') : undefined}
              testID="login-submit"
            />
            {isLoading ? (
              <Text
                accessibilityLiveRegion="polite"
                style={[theme.typography.footnote, styles.status, { color: theme.colors.textSecondary }]}
              >
                {t('account.login.signingIn')}
              </Text>
            ) : null}

            <View style={styles.dividerRow}>
              <View style={[styles.dividerLine, { backgroundColor: theme.colors.hairline }]} />
              <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('account.login.or')}</Text>
              <View style={[styles.dividerLine, { backgroundColor: theme.colors.hairline }]} />
            </View>

            {/* The second way in, for an employee holding a welcome slip and no password yet. */}
            <LiquidButton
              label={t('signin.firstTime')}
              variant="secondary"
              onPress={openWelcome}
              testID="login-welcome-code"
            />

            {/* Which KynexOne installation this is, and the way to another (one app serves every organisation). */}
            <MotionPressable
              accessibilityRole="button"
              accessibilityLabel={t('server.change')}
              onPress={() => navigation.navigate('ServerSetup')}
              hitSlop={4}
              style={styles.serverShell}
              testID="login-change-server"
            >
              {getServer()?.organisationName ? (
                <Text style={[theme.typography.footnote, styles.status, { color: theme.colors.textSecondary }]}>
                  {t('server.connectedTo', { name: getServer()?.organisationName })}
                </Text>
              ) : null}
              <Text style={[theme.typography.footnote, styles.status, styles.link, { color: theme.colors.accent }]}>
                {t('server.change')}
              </Text>
            </MotionPressable>
          </Surface>
        </Animated.View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  fill: { width: '100%', height: '100%' },
  foreground: { zIndex: 1 },
  scroll: { flexGrow: 1 },
  heroImage: {
    position: 'absolute',
    width: 360,
    height: 360,
    end: -110,
    top: -30,
    opacity: 0.9,
    zIndex: 0,
  },
  heroImageCompact: { width: 300, height: 300, end: -100, top: -50 },
  topBar: { flexDirection: 'row', justifyContent: 'flex-end', minHeight: 48 },
  languageChrome: { paddingHorizontal: 6 },
  brandBlock: { width: '100%', maxWidth: 440, alignSelf: 'center', marginTop: 40, marginBottom: 28, gap: 6 },
  brandBlockCompact: { marginTop: 12, marginBottom: 18 },
  wordmark: { fontSize: 22, lineHeight: 28, fontWeight: '800', letterSpacing: 5 },
  tagline: { marginTop: 14, maxWidth: 230 },
  taglineSub: { maxWidth: 230 },
  cardWrap: { width: '100%', maxWidth: 440, alignSelf: 'center', marginTop: 'auto' },
  card: { padding: 22, gap: 4 },
  cardCompact: { padding: 18 },
  cardSub: { marginTop: 4, marginBottom: 18 },
  banner: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    gap: 10,
    borderRadius: 14,
    padding: 12,
    marginBottom: 14,
  },
  bannerTitle: { fontWeight: '600' },
  flex: { flex: 1, minWidth: 0 },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  companyNotice: { marginBottom: 8 },
  forgotShell: { alignSelf: 'flex-end', marginTop: -6, marginBottom: 10 },
  forgot: { minHeight: 44, justifyContent: 'center', paddingHorizontal: 4 },
  link: { fontWeight: '600' },
  serverShell: { marginTop: 14, alignSelf: 'center' },
  status: { textAlign: 'center', marginTop: 10 },
  dividerRow: { flexDirection: 'row', alignItems: 'center', gap: 10, marginVertical: 16 },
  dividerLine: { flex: 1, height: StyleSheet.hairlineWidth },
});
