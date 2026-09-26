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

const loginSchema = z.object({
  tenantId: z.string().trim().min(1, 'Company ID is required'),
  username: z.string().trim().min(1, 'Email or username is required'),
  password: z.string().min(1, 'Password is required'),
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
  const [showPassword, setShowPassword] = useState(false);
  const [focusedField, setFocusedField] = useState<keyof LoginFormData | null>(null);
  const [loginSucceeded, setLoginSucceeded] = useState(false);
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
    formState: { errors },
  } = useForm<LoginFormData>({
    resolver: zodResolver(loginSchema),
    defaultValues: { tenantId: '', username: '', password: '' },
  });

  const loadRememberedTenant = useCallback(async () => {
    const tenant = await appStorage.get<string>('zayra_tenant_id');
    if (tenant) setValue('tenantId', tenant);
  }, [setValue]);

  useEffect(() => {
    void loadRememberedTenant();
  }, [loadRememberedTenant]);

  useEffect(() => {
    if (route.params?.tenantId) setValue('tenantId', route.params.tenantId);
    if (route.params?.email) setValue('username', route.params.email);
    if (route.params?.enrollmentComplete) {
      Alert.alert('MFA enabled', 'Enter your password and authentication code to sign in.');
    }
  }, [route.params, setValue]);

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
      try {
        const outcome = await login(data.username, data.password, data.tenantId);
        if (outcome.kind === 'mfaChallenge') {
          navigation.navigate('MfaChallenge', outcome);
        } else if (outcome.kind === 'mfaEnrollment') {
          navigation.navigate('MfaEnrollment', outcome);
        } else {
          setLoginSucceeded(true);
        }
      } catch {
        // The auth store owns the user-facing error state.
      }
    },
    [login, navigation],
  );

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

          <AnimatedLoginField active={focusedField === 'tenantId'} delay={260}>
          <Controller
            control={control}
            name="tenantId"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('auth.tenantId')}
                icon="business-outline"
                value={value}
                onChangeText={onChange}
                onFocus={() => setFocusedField('tenantId')}
                onBlur={() => { setFocusedField(null); onBlur(); }}
                placeholder="e.g. acme-corp"
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="organization"
                returnKeyType="next"
                error={errors.tenantId?.message}
                accessibilityLabel="Company ID"
              />
            )}
          />
          </AnimatedLoginField>

          <AnimatedLoginField active={focusedField === 'username'} delay={340}>
          <Controller
            control={control}
            name="username"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('auth.username')}
                icon="person-outline"
                value={value}
                onChangeText={onChange}
                onFocus={() => setFocusedField('username')}
                onBlur={() => { setFocusedField(null); onBlur(); }}
                placeholder="Work email or username"
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="username"
                keyboardType="email-address"
                returnKeyType="next"
                error={errors.username?.message}
                accessibilityLabel="Work email or username"
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
                label={t('auth.password')}
                icon="lock-closed-outline"
                value={value}
                onChangeText={onChange}
                onFocus={() => setFocusedField('password')}
                onBlur={() => { setFocusedField(null); onBlur(); }}
                placeholder="Enter your password"
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="current-password"
                secureTextEntry={!showPassword}
                returnKeyType="done"
                onSubmitEditing={handleSubmit(onSubmit)}
                error={errors.password?.message}
                accessibilityLabel="Password"
                trailing={
                  <MotionPressable
                    accessibilityRole="button"
                    accessibilityLabel={showPassword ? 'Hide password' : 'Show password'}
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
          <View style={[styles.cardDivider, { backgroundColor: theme.colors.divider }]} />
          <View style={styles.trustBar}>
            <TrustItem icon="shield-checkmark-outline" label="Secure access" />
            <View style={[styles.trustDivider, { backgroundColor: theme.colors.divider }]} />
            <TrustItem icon="language-outline" label="English · عربي" />
          </View>
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

function TrustItem({ icon, label }: { icon: React.ComponentProps<typeof Ionicons>['name']; label: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.trustItem}>
      <Ionicons accessible={false} name={icon} size={14} color={theme.colors.primary} />
      <Text
        numberOfLines={1}
        style={[theme.typography.micro, { color: theme.colors.textSecondary }]}
      >
        {label}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
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
  cardDivider: { height: StyleSheet.hairlineWidth, marginTop: 24 },
  trustBar: {
    minHeight: 56,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 8,
  },
  trustItem: {
    flex: 1,
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 5,
    paddingHorizontal: 4,
  },
  trustDivider: {
    width: StyleSheet.hairlineWidth,
    height: 20,
  },
});
