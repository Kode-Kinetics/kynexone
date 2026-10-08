import React, { useCallback, useEffect, useState } from 'react';
import {
  Alert,
  Image,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import Animated, {
  cancelAnimation,
  Easing,
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withDelay,
  withRepeat,
  withSequence,
  withSpring,
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
} from '@/components/ui';
import { normalizeEmail, normalizeWorkspace } from '@/auth/publicAuthInput';
import { authFailure } from '@/auth/authStore';
import { isWelcomeCode, normalizeWelcomeCode } from '@/auth/welcomeCode';
import { SignInLanguageToggle } from './SignInLanguageToggle';

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
export default function LoginScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { theme, reduceMotion } = useTheme();
  const insets = useSafeAreaInsets();
  const { width, height, fontScale } = useWindowDimensions();
  const compactLayout = height < 920 || width < 390 || fontScale > 1.1;
  const narrowLayout = width < 370;
  const { login, isLoading, error, clearError } = useAuthStore();
  const mfaEnrolledNotice = useAuthStore((s) => s.mfaEnrolledNotice);
  const consumeMfaEnrolledNotice = useAuthStore((s) => s.consumeMfaEnrolledNotice);
  const [mfaJustEnabled, setMfaJustEnabled] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [focusedField, setFocusedField] = useState<keyof LoginFormData | null>(null);
  const [loginSucceeded, setLoginSucceeded] = useState(false);
  const [showWorkspace, setShowWorkspace] = useState(!!route.params?.tenantId);
  const [companyIdNeeded, setCompanyIdNeeded] = useState(false);
  const entrance = useSharedValue(reduceMotion ? 1 : 0);
  const ambient = useSharedValue(0);
  const sheen = useSharedValue(0);
  const focusGlow = useSharedValue(0);
  const buttonPulse = useSharedValue(0);
  const buttonSuccess = useSharedValue(0);

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
    if (error) Alert.alert('Sign-in failed', error, [{ text: 'OK', onPress: clearError }]);
  }, [clearError, error]);

  useEffect(() => {
    entrance.value = reduceMotion
      ? 1
      : withDelay(90, withTiming(1, { duration: 760, easing: Easing.out(Easing.cubic) }));
    ambient.value = reduceMotion
      ? 0
      : withRepeat(
          withTiming(1, { duration: 11000, easing: Easing.inOut(Easing.sin) }),
          -1,
          true,
        );
    sheen.value = reduceMotion
      ? 0
      : withRepeat(
          withSequence(
            withDelay(1100, withTiming(1, { duration: 1450, easing: Easing.inOut(Easing.quad) })),
            withDelay(3600, withTiming(0, { duration: 0 })),
          ),
          -1,
          false,
        );
  }, [ambient, entrance, reduceMotion, sheen]);

  useEffect(() => {
    focusGlow.value = reduceMotion
      ? focusedField ? 1 : 0
      : withSpring(focusedField ? 1 : 0, { damping: 18, stiffness: 180 });
  }, [focusGlow, focusedField, reduceMotion]);

  useEffect(() => {
    cancelAnimation(buttonPulse);
    if (reduceMotion || !isLoading) {
      buttonPulse.value = 0;
    } else {
      buttonPulse.value = withRepeat(
        withSequence(
          withTiming(1, { duration: 620, easing: Easing.inOut(Easing.quad) }),
          withTiming(0, { duration: 620, easing: Easing.inOut(Easing.quad) }),
        ),
        -1,
        false,
      );
    }
    buttonSuccess.value = reduceMotion
      ? loginSucceeded ? 1 : 0
      : withSpring(loginSucceeded ? 1 : 0, { damping: 12, stiffness: 190 });
  }, [buttonPulse, buttonSuccess, isLoading, loginSucceeded, reduceMotion]);

  const brandMotion = useAnimatedStyle(() => ({
    opacity: entrance.value,
    transform: [
      { perspective: 800 },
      { translateX: interpolate(ambient.value, [0, 1], [-2, 3]) },
      { translateY: interpolate(entrance.value, [0, 1], [-18, 0]) },
      { rotateY: `${interpolate(ambient.value, [0, 1], [-0.7, 0.7])}deg` },
    ],
  }));
  const cardMotion = useAnimatedStyle(() => ({
    opacity: interpolate(entrance.value, [0, 0.22, 1], [0, 0, 1]),
    transform: [
      { perspective: 1100 },
      { translateY: interpolate(entrance.value, [0, 1], [54, 0]) },
      { rotateX: `${interpolate(entrance.value, [0, 1], [7, 0])}deg` },
      { rotateY: `${interpolate(ambient.value, [0, 1], [-0.45, 0.45])}deg` },
      { scale: interpolate(entrance.value, [0, 1], [0.965, 1]) },
    ],
  }));
  const orbMotion = useAnimatedStyle(() => ({
    transform: [
      { translateX: interpolate(ambient.value, [0, 1], [-8, 10]) },
      { translateY: interpolate(ambient.value, [0, 1], [-4, 12]) },
      { scale: interpolate(ambient.value, [0, 1], [0.98, 1.025]) },
      { rotateZ: `${interpolate(ambient.value, [0, 1], [-0.35, 0.35])}deg` },
    ],
  }));
  const cardGlow = useAnimatedStyle(() => ({
    opacity: interpolate(focusGlow.value, [0, 1], [0, 0.72]),
    transform: [{ scale: interpolate(focusGlow.value, [0, 1], [0.985, 1]) }],
  }));
  const sheenMotion = useAnimatedStyle(() => ({
    opacity: interpolate(sheen.value, [0, 0.08, 0.84, 1], [0, 0.46, 0.34, 0]),
    transform: [
      { translateX: interpolate(sheen.value, [0, 1], [-150, width + 80]) },
      { rotateZ: '-14deg' },
    ],
  }));
  const buttonMotion = useAnimatedStyle(() => ({
    transform: [
      { perspective: 700 },
      { translateY: interpolate(buttonPulse.value, [0, 1], [0, -2]) },
      { scale: 1 + buttonSuccess.value * 0.025 + buttonPulse.value * 0.012 },
      { rotateX: `${buttonPulse.value * -0.7}deg` },
    ],
  }));
  const loginStatusMotion = useAnimatedStyle(() => ({
    opacity: interpolate(buttonPulse.value, [0, 1], [0.72, 1]),
    transform: [
      { translateY: interpolate(buttonPulse.value, [0, 1], [2, 0]) },
      { scale: interpolate(buttonPulse.value, [0, 1], [0.99, 1]) },
    ],
  }));
  const loginProgressMotion = useAnimatedStyle(() => ({
    transform: [
      { translateX: interpolate(buttonPulse.value, [0, 1], [-112, 112]) },
      { scaleX: interpolate(buttonPulse.value, [0, 1], [0.42, 0.9]) },
    ],
  }));
  const onSubmit = useCallback(
    async (data: LoginFormData) => {
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
          setLoginSucceeded(true);
          // A live reset code is reported by ResetCodeBanner in the signed-in shell (Amendment 3, F1).
        }
      } catch (error) {
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
        }
        // Anything else: the auth store owns the user-facing error state.
      }
    },
    [login, mfaJustEnabled, navigation, setError, setValue, showWorkspace],
  );

  const openWelcome = useCallback(() => {
    const email = normalizeEmail(getValues('username'));
    const workspace = showWorkspace ? normalizeWorkspace(getValues('tenantId')) : '';
    navigation.navigate('Welcome', { email: email || undefined, workspace: workspace || undefined });
  }, [getValues, navigation, showWorkspace]);

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop subtle />
      <Animated.View pointerEvents="none" style={[styles.heroOrb, orbMotion]}>
        <Image
          accessible={false}
          source={require('../../../assets/login-cinematic-orb.png')}
          resizeMode="cover"
          style={styles.heroOrbImage}
        />
      </Animated.View>
      <ScrollView
        style={styles.foreground}
        contentContainerStyle={[
          styles.scroll,
          compactLayout && styles.scrollCompact,
          {
            paddingTop: compactLayout
              ? Math.max(insets.top + 10, 34)
              : Math.max(insets.top + 22, 50),
            paddingBottom: compactLayout
              ? Math.max(insets.bottom + 14, 24)
              : Math.max(insets.bottom + 22, 32),
            paddingHorizontal: narrowLayout ? 14 : 20,
          },
        ]}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <Animated.View style={[styles.brandBlock, compactLayout && styles.brandBlockCompact, brandMotion]}>
          <Text
            style={[
              styles.wordmark,
              compactLayout && styles.wordmarkCompact,
              { color: theme.colors.text },
            ]}
          >
            KYNEXONE
          </Text>
          <Text
            style={[
              theme.typography.caption,
              styles.tagline,
              compactLayout && styles.taglineCompact,
              { color: theme.colors.textSecondary },
            ]}
          >
            One intelligent workspace for your workforce
          </Text>
          <LinearGradient colors={['#23D4E9', '#394EFF']} style={styles.brandAccent} />
          {!compactLayout ? <Text style={styles.brandMotto}>People.{`\n`}Work.{`\n`}Forward.</Text> : null}
        </Animated.View>
        <Animated.View style={[styles.cardWrap, cardMotion]}>
          <Animated.View pointerEvents="none" style={[styles.focusHalo, cardGlow]} />
          <GlassSurface
          radius={34}
          style={styles.formCard}
          contentStyle={[
            styles.formContent,
            compactLayout && styles.formContentCompact,
          ]}
          tintColor={theme.isDark ? 'rgba(23,52,110,0.40)' : 'rgba(255,255,255,0.58)'}
          intensity={58}
        >
          <Animated.View pointerEvents="none" style={[styles.cardSheen, sheenMotion]}>
            <LinearGradient
              colors={['transparent', 'rgba(255,255,255,0.78)', 'transparent']}
              start={{ x: 0, y: 0.5 }}
              end={{ x: 1, y: 0.5 }}
              style={StyleSheet.absoluteFill}
            />
          </Animated.View>
          <View style={[styles.formHeading, compactLayout && styles.formHeadingCompact]}>
            <Text style={[theme.typography.h1, { color: theme.colors.text }]}>Welcome back</Text>
            <Text style={[theme.typography.body, { color: theme.colors.textSecondary, marginTop: 6 }]}>
              Attendance, leave, payroll and approvals in one place.
            </Text>
          </View>

          {mfaJustEnabled ? (
            <View
              style={[
                styles.mfaNotice,
                { backgroundColor: theme.colors.cyan + '14', borderColor: theme.colors.cyan + '40' },
              ]}
              accessibilityRole="alert"
            >
              <Ionicons name="shield-checkmark-outline" size={18} color={theme.colors.cyan} />
              <View style={styles.mfaNoticeText}>
                <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{t('mfa.doneTitle')}</Text>
                <Text style={[theme.typography.caption, styles.mfaNoticeBody, { color: theme.colors.textSecondary }]}>
                  {t('mfa.doneSignInAgain')}
                </Text>
              </View>
            </View>
          ) : null}

          <AnimatedLoginField active={focusedField === 'username'} delay={340}>
          <Controller
            control={control}
            name="username"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('signin.email')}
                icon="mail-outline"
                value={value}
                onChangeText={onChange}
                onFocus={() => setFocusedField('username')}
                onBlur={() => { setFocusedField(null); onBlur(); }}
                placeholder={t('signin.emailPlaceholder')}
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="username"
                textContentType="username"
                keyboardType="email-address"
                returnKeyType="next"
                error={errors.username?.message ? t(errors.username.message as 'signin.emailRequired') : undefined}
                accessibilityLabel={t('signin.email')}
              />
            )}
          />
          </AnimatedLoginField>
          <AnimatedLoginField active={focusedField === 'password'} delay={420}>
          <Controller
            control={control}
            name="password"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('signin.password')}
                icon="lock-closed-outline"
                value={value}
                onChangeText={onChange}
                onFocus={() => setFocusedField('password')}
                onBlur={() => { setFocusedField(null); onBlur(); }}
                placeholder={t('signin.passwordPlaceholder')}
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="current-password"
                spellCheck={false}
                secureTextEntry={!showPassword}
                returnKeyType="done"
                onSubmitEditing={handleSubmit(onSubmit)}
                error={errors.password?.message ? t(errors.password.message as 'signin.passwordRequired') : undefined}
                accessibilityLabel={t('signin.password')}
                trailing={
                  <MotionPressable
                    accessibilityRole="button"
                    accessibilityLabel={showPassword ? t('signin.hidePassword') : t('signin.showPassword')}
                    onPress={() => setShowPassword((current) => !current)}
                    haptic="selection"
                    contentStyle={styles.passwordToggle}
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
          </AnimatedLoginField>

          {showWorkspace ? (
            <AnimatedLoginField active={focusedField === 'tenantId'} delay={0}>
            {companyIdNeeded ? (
              <Text accessibilityRole="alert" style={[theme.typography.caption, styles.companyNotice, { color: theme.colors.text }]}>
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
                  onFocus={() => setFocusedField('tenantId')}
                  onBlur={() => { setFocusedField(null); onBlur(); }}
                  placeholder="your-company"
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="organization"
                  autoFocus={companyIdNeeded}
                  returnKeyType="done"
                  onSubmitEditing={handleSubmit(onSubmit)}
                  error={errors.tenantId?.message ? t(errors.tenantId.message as 'signin.companyIdRequired') : undefined}
                  accessibilityLabel={t('signin.companyId')}
                />
              )}
            />
            </AnimatedLoginField>
          ) : null}

          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={t('auth.forgotPassword')}
            onPress={() => navigation.navigate('ForgotPassword')}
            haptic="selection"
            contentStyle={styles.forgotPressable}
          >
            <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
              {t('auth.forgotPassword')}
            </Text>
          </MotionPressable>

          <Animated.View style={buttonMotion}>
          <LiquidButton
            label={loginSucceeded ? 'Signed in' : t('auth.login')}
            icon={loginSucceeded ? 'checkmark-circle-outline' : 'log-in-outline'}
            onPress={handleSubmit(onSubmit)}
            loading={isLoading}
            disabled={isLoading}
            variant={loginSucceeded ? 'success' : 'primary'}
            style={[styles.submit, compactLayout && styles.submitCompact]}
            testID="login-submit"
          />
          </Animated.View>
          {isLoading ? (
            <Animated.View
              accessibilityLiveRegion="polite"
              accessibilityLabel="Signing in securely"
              style={[styles.loginStatus, loginStatusMotion]}
            >
              <View style={[styles.loginStatusIcon, { backgroundColor: theme.colors.surfaceSoft }]}>
                <Ionicons name="shield-checkmark-outline" size={18} color={theme.colors.primary} />
              </View>
              <View style={styles.loginStatusCopy}>
                <Text style={[theme.typography.caption, styles.loginStatusTitle, { color: theme.colors.text }]}>
                  Securing your workspace
                </Text>
                <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
                  Verifying access and preparing your dashboard
                </Text>
                <View style={[styles.loginProgressTrack, { backgroundColor: theme.colors.divider }]}>
                  <Animated.View style={[styles.loginProgressBar, loginProgressMotion]}>
                    <LinearGradient
                      colors={['#23D4E9', '#4F75FF', '#9067F9']}
                      start={{ x: 0, y: 0.5 }}
                      end={{ x: 1, y: 0.5 }}
                      style={StyleSheet.absoluteFill}
                    />
                  </Animated.View>
                </View>
              </View>
            </Animated.View>
          ) : null}
          {/* The second way in, for an employee holding a welcome slip and no password yet. */}
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={t('signin.firstTime')}
            onPress={openWelcome}
            haptic="selection"
            contentStyle={[styles.welcomeButton, { borderColor: theme.colors.border }]}
            testID="login-welcome-code"
          >
            <Text style={[theme.typography.bodyStrong, styles.welcomeButtonText, { color: theme.colors.text }]}>
              {t('signin.firstTime')}
            </Text>
          </MotionPressable>
          <View style={[styles.cardDivider, { backgroundColor: theme.colors.divider }]} />
          <SignInLanguageToggle />
        </GlassSurface>
        </Animated.View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function AnimatedLoginField({
  active,
  delay,
  children,
}: {
  active: boolean;
  delay: number;
  children: React.ReactNode;
}) {
  const { reduceMotion } = useTheme();
  const entered = useSharedValue(reduceMotion ? 1 : 0);
  const focused = useSharedValue(active ? 1 : 0);

  useEffect(() => {
    entered.value = reduceMotion
      ? 1
      : withDelay(delay, withTiming(1, { duration: 520, easing: Easing.out(Easing.cubic) }));
  }, [delay, entered, reduceMotion]);

  useEffect(() => {
    focused.value = reduceMotion
      ? active ? 1 : 0
      : withSpring(active ? 1 : 0, { damping: 17, stiffness: 210, mass: 0.72 });
  }, [active, focused, reduceMotion]);

  const motion = useAnimatedStyle(() => ({
    opacity: entered.value,
    transform: [
      { perspective: 700 },
      { translateX: interpolate(entered.value, [0, 1], [18, 0]) },
      { translateY: interpolate(focused.value, [0, 1], [0, -3]) },
      { rotateY: `${interpolate(entered.value, [0, 1], [2.5, 0])}deg` },
      { scale: interpolate(focused.value, [0, 1], [1, 1.012]) },
    ],
  }));

  return <Animated.View style={motion}>{children}</Animated.View>;
}

const styles = StyleSheet.create({
  mfaNotice: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    gap: 10,
    borderWidth: 1,
    borderRadius: 14,
    padding: 12,
    marginBottom: 16,
  },
  mfaNoticeText: { flex: 1 },
  mfaNoticeBody: { marginTop: 2, lineHeight: 18 },
  root: { flex: 1 },
  foreground: { zIndex: 1 },
  scroll: {
    flexGrow: 1,
    justifyContent: 'flex-end',
  },
  scrollCompact: { justifyContent: 'flex-start' },
  heroOrb: {
    position: 'absolute', width: 510, height: 510,
    right: -126, top: -68, zIndex: 0,
  },
  heroOrbImage: { width: '100%', height: '100%' },
  brandBlock: { alignItems: 'flex-start', width: '100%', maxWidth: 440, alignSelf: 'center', marginBottom: 24, paddingLeft: 8 },
  brandBlockCompact: { marginBottom: 14 },
  wordmark: { fontSize: 24, lineHeight: 30, fontWeight: '800', letterSpacing: 6.5 },
  wordmarkCompact: { fontSize: 22, lineHeight: 26, letterSpacing: 4.4 },
  tagline: { marginTop: 12, maxWidth: 190, fontSize: 17, lineHeight: 24 },
  taglineCompact: { marginTop: 6, maxWidth: 180 },
  brandAccent: { width: 48, height: 4, borderRadius: 2, marginTop: 18 },
  brandMotto: { marginTop: 42, color: 'rgba(255,255,255,0.72)', fontSize: 18, lineHeight: 24, letterSpacing: 0.6 },
  cardWrap: { width: '100%', maxWidth: 440, alignSelf: 'center' },
  focusHalo: { position: 'absolute', top: -6, right: -6, bottom: -6, left: -6, borderRadius: 40, backgroundColor: 'rgba(64,114,255,0.22)' },
  cardSheen: {
    position: 'absolute', top: -90, bottom: -90, left: 0, width: 74,
  },
  formCard: { width: '100%', minHeight: 0 },
  formContent: { paddingHorizontal: 26, paddingTop: 30, paddingBottom: 16 },
  formContentCompact: { paddingHorizontal: 18, paddingVertical: 18 },
  formHeading: { marginBottom: 22 },
  formHeadingCompact: { marginBottom: 16 },
  passwordToggle: {
    width: 44,
    height: 44,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 14,
  },
  forgotPressable: {
    alignSelf: 'flex-end',
    paddingVertical: 6,
    paddingHorizontal: 4,
  },
  submit: { marginTop: 16 },
  submitCompact: { marginTop: 12 },
  loginStatus: {
    minHeight: 66,
    marginTop: 12,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 18,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    backgroundColor: 'rgba(255,255,255,0.16)',
  },
  loginStatusIcon: {
    width: 38,
    height: 38,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  loginStatusCopy: { flex: 1, minWidth: 0 },
  loginStatusTitle: { fontWeight: '700', marginBottom: 1 },
  loginProgressTrack: {
    height: 3,
    marginTop: 7,
    borderRadius: 2,
    overflow: 'hidden',
  },
  loginProgressBar: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    left: '35%',
    width: '30%',
    borderRadius: 2,
  },
  cardDivider: { height: StyleSheet.hairlineWidth, marginTop: 18 },
  companyNotice: { marginBottom: 8, lineHeight: 18 },
  welcomeButton: {
    marginTop: 12,
    minHeight: 50,
    borderRadius: 16,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  welcomeButtonText: { textAlign: 'center' },
});
