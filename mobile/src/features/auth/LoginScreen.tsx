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
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();
  const { width, height, fontScale } = useWindowDimensions();
  const compactLayout = height < 920 || width < 390 || fontScale > 1.1;
  const narrowLayout = width < 370;
  const { login, isLoading, error, clearError } = useAuthStore();
  const [showPassword, setShowPassword] = useState(false);

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
  const onSubmit = useCallback(
    async (data: LoginFormData) => {
      try {
        const outcome = await login(data.username, data.password, data.tenantId);
        if (outcome.kind === 'mfaChallenge') {
          navigation.navigate('MfaChallenge', outcome);
        } else if (outcome.kind === 'mfaEnrollment') {
          navigation.navigate('MfaEnrollment', outcome);
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
      <LiquidBackdrop />
      <ScrollView
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
        <View style={[styles.brandBlock, compactLayout && styles.brandBlockCompact]}>
          <GlassSurface
            radius={compactLayout ? 22 : 26}
            style={[styles.logoSurface, compactLayout && styles.logoSurfaceCompact]}
            contentStyle={styles.logoContent}
            tintColor={theme.isDark ? 'rgba(47,107,255,0.26)' : 'rgba(255,255,255,0.50)'}
          >
            <Image
              accessible={false}
              source={
                Platform.OS === 'ios'
                  ? { uri: 'KynexLoomMark' }
                  : require('../../../assets/adaptive-icon.png')
              }
              style={styles.logoMark}
              resizeMode="contain"
            />
          </GlassSurface>
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
        </View>
        <GlassSurface
          radius={theme.radius.xxl}
          style={styles.formCard}
          contentStyle={[
            styles.formContent,
            compactLayout && styles.formContentCompact,
          ]}
          tintColor={theme.isDark ? 'rgba(10,31,70,0.32)' : 'rgba(255,255,255,0.45)'}
        >
          <View style={[styles.formHeading, compactLayout && styles.formHeadingCompact]}>
            <Text style={[theme.typography.h1, { color: theme.colors.text }]}>Welcome back</Text>
            <Text style={[theme.typography.body, { color: theme.colors.textSecondary, marginTop: 6 }]}>
              Attendance, leave, payroll and approvals in one place.
            </Text>
          </View>

          <Controller
            control={control}
            name="tenantId"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('auth.tenantId')}
                icon="business-outline"
                value={value}
                onChangeText={onChange}
                onBlur={onBlur}
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

          <Controller
            control={control}
            name="username"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('auth.username')}
                icon="person-outline"
                value={value}
                onChangeText={onChange}
                onBlur={onBlur}
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
          <Controller
            control={control}
            name="password"
            render={({ field: { onChange, value, onBlur } }) => (
              <GlassTextField
                label={t('auth.password')}
                icon="lock-closed-outline"
                value={value}
                onChangeText={onChange}
                onBlur={onBlur}
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

          <LiquidButton
            label={t('auth.login')}
            icon="log-in-outline"
            onPress={handleSubmit(onSubmit)}
            loading={isLoading}
            disabled={isLoading}
            style={[styles.submit, compactLayout && styles.submitCompact]}
            testID="login-submit"
          />
        </GlassSurface>
        {!compactLayout ? (
          <>
            <GlassSurface
              elevated={false}
              radius={18}
              style={styles.trustBarSurface}
              contentStyle={styles.trustBar}
            >
              <TrustItem icon="shield-checkmark-outline" label="Secure" />
              <View style={[styles.trustDivider, { backgroundColor: theme.colors.divider }]} />
              <TrustItem icon="language-outline" label="EN · عربي" />
              <View style={[styles.trustDivider, { backgroundColor: theme.colors.divider }]} />
              <TrustItem icon="sparkles-outline" label="AI ready" />
            </GlassSurface>

            <Text
              style={[
                theme.typography.micro,
                styles.footer,
                { color: theme.colors.textMuted },
              ]}
            >
              Privacy-first · GCC ready
            </Text>
          </>
        ) : null}
      </ScrollView>
    </KeyboardAvoidingView>
  );
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
  scroll: {
    flexGrow: 1,
    justifyContent: 'center',
  },
  scrollCompact: { justifyContent: 'flex-start' },
  brandBlock: { alignItems: 'center', marginBottom: 26 },
  brandBlockCompact: { marginBottom: 16 },
  logoSurface: { width: 76, height: 76, marginBottom: 14 },
  logoSurfaceCompact: { width: 60, height: 60, marginBottom: 10 },
  logoContent: { alignItems: 'center', justifyContent: 'center', padding: 7 },
  logoMark: { flex: 1, width: '100%' },
  wordmark: { fontSize: 25, lineHeight: 30, fontWeight: '800', letterSpacing: 5.5 },
  wordmarkCompact: { fontSize: 22, lineHeight: 26, letterSpacing: 4.4 },
  tagline: { marginTop: 6, textAlign: 'center', maxWidth: 310 },
  taglineCompact: { marginTop: 4, maxWidth: 280 },
  formCard: { width: '100%', maxWidth: 440, alignSelf: 'center' },
  formContent: { paddingHorizontal: 22, paddingVertical: 24 },
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
  trustBarSurface: {
    width: '100%',
    maxWidth: 440,
    alignSelf: 'center',
    marginTop: 14,
  },
  trustBarSurfaceCompact: { marginTop: 10 },
  trustBar: {
    minHeight: 44,
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
  footer: {
    textAlign: 'center',
    marginTop: 12,
    letterSpacing: 0.2,
  },
  footerCompact: { marginTop: 8 },
});
