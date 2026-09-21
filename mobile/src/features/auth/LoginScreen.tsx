// ============================================================
// ZAYRA MOBILE — Login Screen
// ============================================================

import React, { useState, useEffect, useCallback } from 'react';
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  ActivityIndicator,
  Alert,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import { useForm, Controller } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { useAuthStore } from '@/auth/authStore';
import { appStorage } from '@/storage';
import { COLORS } from '@/config';
import { useTranslation } from 'react-i18next';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';

const loginSchema = z.object({
  tenantId: z.string().min(1, 'Company ID is required'),
  username: z.string().min(1, 'Username is required'),
  password: z.string().min(1, 'Password is required'),
});

type LoginFormData = z.infer<typeof loginSchema>;

type Props = NativeStackScreenProps<AuthStackParamList, 'Login'>;

export default function LoginScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
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
    if (route?.params?.tenantId) setValue('tenantId', route.params.tenantId);
    if (route?.params?.email) setValue('username', route.params.email);
    if (route?.params?.enrollmentComplete) {
      Alert.alert('MFA enabled', 'Enter your password and new authentication code to sign in.');
    }
  }, [route?.params, setValue]);

  useEffect(() => {
    if (error) {
      Alert.alert('Login Failed', error, [{ text: 'OK', onPress: clearError }]);
    }
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
        // Authenticated sessions switch RootNavigator automatically.
      } catch {
        // Error handled by store
      }
    },
    [login, navigation]
  );

  return (
    <KeyboardAvoidingView
      style={styles.container}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LinearGradient
        colors={['#0B1020', '#0F1830', '#0B1020']}
        style={StyleSheet.absoluteFill}
      />

      {/* Background pattern */}
      <View style={styles.bgPattern} />

      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        showsVerticalScrollIndicator={false}
      >
        {/* Logo */}
        <View style={styles.logoContainer}>
          <View style={styles.logoMark}>
            <Text style={styles.logoZ}>K</Text>
          </View>
          <Text style={styles.logoText}>KYNEXONE</Text>
          <Text style={styles.logoTagline}>AI Workforce Intelligence</Text>
        </View>

        {/* Card */}
        <View style={styles.card}>
          <Text style={styles.cardTitle}>{t('auth.login')}</Text>
          <Text style={styles.cardSubtitle}>Access your workforce portal</Text>

          {/* Company ID */}
          <View style={styles.fieldGroup}>
            <Text style={styles.label}>{t('auth.tenantId')}</Text>
            <Controller
              control={control}
              name="tenantId"
              render={({ field: { onChange, value, onBlur } }) => (
                <View style={[styles.inputWrapper, errors.tenantId && styles.inputError]}>
                  <Ionicons name="business-outline" size={18} color={COLORS.muted} />
                  <TextInput
                    style={styles.input}
                    placeholder="e.g. acme-corp"
                    placeholderTextColor={COLORS.muted}
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    autoCapitalize="none"
                    autoCorrect={false}
                    returnKeyType="next"
                  />
                </View>
              )}
            />
            {errors.tenantId && (
              <Text style={styles.errorText}>{errors.tenantId.message}</Text>
            )}
          </View>

          {/* Username */}
          <View style={styles.fieldGroup}>
            <Text style={styles.label}>{t('auth.username')}</Text>
            <Controller
              control={control}
              name="username"
              render={({ field: { onChange, value, onBlur } }) => (
                <View style={[styles.inputWrapper, errors.username && styles.inputError]}>
                  <Ionicons name="person-outline" size={18} color={COLORS.muted} />
                  <TextInput
                    style={styles.input}
                    placeholder="Enter your username"
                    placeholderTextColor={COLORS.muted}
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    autoCapitalize="none"
                    autoCorrect={false}
                    returnKeyType="next"
                  />
                </View>
              )}
            />
            {errors.username && (
              <Text style={styles.errorText}>{errors.username.message}</Text>
            )}
          </View>

          {/* Password */}
          <View style={styles.fieldGroup}>
            <Text style={styles.label}>{t('auth.password')}</Text>
            <Controller
              control={control}
              name="password"
              render={({ field: { onChange, value, onBlur } }) => (
                <View style={[styles.inputWrapper, errors.password && styles.inputError]}>
                  <Ionicons name="lock-closed-outline" size={18} color={COLORS.muted} />
                  <TextInput
                    style={styles.input}
                    placeholder="Enter your password"
                    placeholderTextColor={COLORS.muted}
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    secureTextEntry={!showPassword}
                    returnKeyType="done"
                    onSubmitEditing={handleSubmit(onSubmit)}
                  />
                  <TouchableOpacity onPress={() => setShowPassword(!showPassword)}>
                    <Ionicons
                      name={showPassword ? 'eye-off-outline' : 'eye-outline'}
                      size={18}
                      color={COLORS.muted}
                    />
                  </TouchableOpacity>
                </View>
              )}
            />
            {errors.password && (
              <Text style={styles.errorText}>{errors.password.message}</Text>
            )}
          </View>

          {/* Forgot password */}
          <TouchableOpacity
            style={styles.forgotBtn}
            onPress={() => navigation.navigate('ForgotPassword')}
          >
            <Text style={styles.forgotText}>{t('auth.forgotPassword')}</Text>
          </TouchableOpacity>

          {/* Login button */}
          <TouchableOpacity
            style={[styles.loginBtn, isLoading && styles.loginBtnDisabled]}
            onPress={handleSubmit(onSubmit)}
            disabled={isLoading}
            activeOpacity={0.85}
          >
            <LinearGradient
              colors={['#2F6BFF', '#1A4FCC']}
              style={styles.loginBtnGradient}
              start={{ x: 0, y: 0 }}
              end={{ x: 1, y: 0 }}
            >
              {isLoading ? (
                <ActivityIndicator color="#fff" size="small" />
              ) : (
                <>
                  <Text style={styles.loginBtnText}>{t('auth.login')}</Text>
                  <Ionicons name="arrow-forward" size={18} color="#fff" />
                </>
              )}
            </LinearGradient>
          </TouchableOpacity>


        </View>

        {/* Footer */}
        <Text style={styles.footer}>
          Secured by KynexOne · Enterprise Grade · GCC Compliant
        </Text>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.navy },
  bgPattern: {
    position: 'absolute',
    top: -100,
    right: -100,
    width: 400,
    height: 400,
    borderRadius: 200,
    backgroundColor: 'rgba(47, 107, 255, 0.06)',
  },
  scroll: {
    flexGrow: 1,
    justifyContent: 'center',
    padding: 24,
    paddingTop: 60,
  },
  logoContainer: { alignItems: 'center', marginBottom: 40 },
  logoMark: {
    width: 60,
    height: 60,
    borderRadius: 16,
    backgroundColor: COLORS.blue,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 12,
    shadowColor: COLORS.blue,
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.5,
    shadowRadius: 16,
    elevation: 12,
  },
  logoZ: { fontSize: 32, fontWeight: '800', color: '#fff', letterSpacing: -1 },
  logoText: {
    fontSize: 28,
    fontWeight: '800',
    color: '#fff',
    letterSpacing: 6,
    marginBottom: 4,
  },
  logoTagline: { fontSize: 12, color: 'rgba(255,255,255,0.4)', letterSpacing: 1 },
  card: {
    backgroundColor: 'rgba(255,255,255,0.04)',
    borderRadius: 20,
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.08)',
    padding: 24,
    marginBottom: 24,
  },
  cardTitle: { fontSize: 22, fontWeight: '700', color: '#fff', marginBottom: 4 },
  cardSubtitle: { fontSize: 14, color: 'rgba(255,255,255,0.5)', marginBottom: 24 },
  fieldGroup: { marginBottom: 16 },
  label: { fontSize: 13, fontWeight: '600', color: 'rgba(255,255,255,0.7)', marginBottom: 8 },
  inputWrapper: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: 'rgba(255,255,255,0.06)',
    borderRadius: 12,
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.1)',
    paddingHorizontal: 14,
    paddingVertical: 12,
    gap: 10,
  },
  inputError: { borderColor: COLORS.error },
  input: { flex: 1, fontSize: 15, color: '#fff' },
  errorText: { fontSize: 12, color: COLORS.error, marginTop: 4 },
  forgotBtn: { alignSelf: 'flex-end', marginBottom: 20 },
  forgotText: { fontSize: 13, color: COLORS.cyan, fontWeight: '500' },
  loginBtn: { borderRadius: 14, overflow: 'hidden', marginBottom: 16 },
  loginBtnDisabled: { opacity: 0.7 },
  loginBtnGradient: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 16,
    gap: 8,
  },
  loginBtnText: { fontSize: 16, fontWeight: '700', color: '#fff', letterSpacing: 0.3 },
  footer: { textAlign: 'center', fontSize: 11, color: 'rgba(255,255,255,0.25)' },
});
