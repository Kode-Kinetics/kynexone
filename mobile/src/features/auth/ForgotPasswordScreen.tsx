import React, { useEffect, useState } from 'react';
import {
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation } from '@react-navigation/native';
import { authApi } from '@/api/adapters';
import { appStorage } from '@/storage';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const MIN_PASSWORD_LENGTH = 10;

export default function ForgotPasswordScreen() {
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const [step, setStep] = useState<'email' | 'reset'>('email');
  const [email, setEmail] = useState('');
  const [tenantSlug, setTenantSlug] = useState('');
  const [token, setToken] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [showPw, setShowPw] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  useEffect(() => {
    appStorage
      .get<string>('zayra_tenant_id')
      .then((tenant) => tenant && setTenantSlug(tenant))
      .catch(() => undefined);
  }, []);

  const requestReset = async () => {
    if (!email.trim()) return Alert.alert('Work email required', 'Enter your work email.');
    if (!tenantSlug.trim()) return Alert.alert('Company ID required', 'Enter your Company ID.');

    setLoading(true);
    try {
      await authApi.forgotPassword({
        email: email.trim(),
        tenantSlug: tenantSlug.trim(),
      });
      setStep('reset');
      Alert.alert(
        'Check your email',
        'If the account exists, a password-reset code has been sent to the registered email address.',
      );
    } catch (error: any) {
      Alert.alert(
        'Could not send reset code',
        error?.response?.data?.message || error?.message || 'Please try again.',
      );
    } finally {
      setLoading(false);
    }
  };

  const submitReset = async () => {
    if (!token.trim()) return Alert.alert('Reset code required', 'Enter the code from your email.');
    if (newPassword.length < MIN_PASSWORD_LENGTH) {
      return Alert.alert(
        'Password is too short',
        'Use at least ' + MIN_PASSWORD_LENGTH + ' characters.',
      );
    }
    if (newPassword !== confirmPassword) {
      return Alert.alert('Passwords do not match', 'Re-enter the same new password.');
    }

    setLoading(true);
    try {
      await authApi.resetPassword({
        email: email.trim(),
        token: token.trim(),
        newPassword,
        tenantSlug: tenantSlug.trim(),
      });
      Alert.alert(
        'Password reset',
        'Your new password is ready. Sign in to continue.',
        [{ text: 'Sign in', onPress: () => navigation.navigate('Login') }],
      );
    } catch (error: any) {
      Alert.alert(
        'Could not reset password',
        error?.response?.data?.message || 'The code may be invalid or expired.',
      );
    } finally {
      setLoading(false);
    }
  };

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
          eyebrow="Account recovery"
          title={step === 'email' ? 'Reset your password' : 'Set a new password'}
          subtitle={
            step === 'email'
              ? 'We’ll verify your Company ID and work email before sending a reset code.'
              : 'Enter the code from your email and choose a new password.'
          }
          onBack={() => navigation.goBack()}
          backLabel="Back to sign in"
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            {step === 'email' ? (
              <>
                <GlassTextField
                  label="Company ID"
                  icon="business-outline"
                  value={tenantSlug}
                  onChangeText={setTenantSlug}
                  placeholder="e.g. acme-corp"
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="organization"
                />
                <GlassTextField
                  label="Work email"
                  icon="mail-outline"
                  value={email}
                  onChangeText={setEmail}
                  placeholder="you@company.com"
                  autoCapitalize="none"
                  autoCorrect={false}
                  keyboardType="email-address"
                  autoComplete="email"
                  textContentType="emailAddress"
                />
                <LiquidButton
                  label="Send reset code"
                  icon="mail-unread-outline"
                  onPress={() => void requestReset()}
                  loading={loading}
                  disabled={loading}
                />
              </>
            ) : (
              <>
                <GlassSurface
                  elevated={false}
                  radius={16}
                  contentStyle={styles.deliveryNote}
                >
                  <View style={[styles.deliveryIcon, { backgroundColor: theme.colors.success + '18' }]}>
                    <Ionicons name="checkmark" size={18} color={theme.colors.success} />
                  </View>
                  <View style={styles.deliveryCopy}>
                    <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                      Code requested
                    </Text>
                    <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                      Use the newest reset code sent to {email || 'your work email'}.
                    </Text>
                  </View>
                </GlassSurface>

                <GlassTextField
                  label="Reset code"
                  icon="keypad-outline"
                  value={token}
                  onChangeText={setToken}
                  placeholder="Enter reset code"
                  autoCapitalize="none"
                  autoCorrect={false}
                />
                <GlassTextField
                  label="New password"
                  icon="key-outline"
                  value={newPassword}
                  onChangeText={setNewPassword}
                  placeholder="Minimum 10 characters"
                  secureTextEntry={!showPw}
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="new-password"
                  textContentType="newPassword"
                  trailing={
                    <VisibilityToggle
                      visible={showPw}
                      onPress={() => setShowPw((current) => !current)}
                    />
                  }
                />
                <GlassTextField
                  label="Confirm password"
                  icon="checkmark-circle-outline"
                  value={confirmPassword}
                  onChangeText={setConfirmPassword}
                  placeholder="Re-enter new password"
                  secureTextEntry={!showConfirm}
                  autoCapitalize="none"
                  autoCorrect={false}
                  autoComplete="new-password"
                  textContentType="newPassword"
                  trailing={
                    <VisibilityToggle
                      visible={showConfirm}
                      onPress={() => setShowConfirm((current) => !current)}
                    />
                  }
                />

                <LiquidButton
                  label="Reset password"
                  icon="shield-checkmark-outline"
                  onPress={() => void submitReset()}
                  loading={loading}
                  disabled={loading}
                />

                <MotionPressable
                  accessibilityRole="button"
                  accessibilityLabel="Request another reset code"
                  onPress={() => setStep('email')}
                  haptic="selection"
                  contentStyle={styles.resendButton}
                >
                  <Ionicons name="refresh-outline" size={17} color={theme.colors.primary} />
                  <Text style={[theme.typography.caption, styles.resendText, { color: theme.colors.primary }]}>
                    Request another code
                  </Text>
                </MotionPressable>
              </>
            )}
          </GlassSurface>

          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            For your privacy, KynexOne does not confirm whether an email address exists.
          </Text>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function VisibilityToggle({ visible, onPress }: { visible: boolean; onPress: () => void }) {
  const { theme } = useTheme();
  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={visible ? 'Hide password' : 'Show password'}
      onPress={onPress}
      haptic="selection"
      contentStyle={styles.visibilityButton}
    >
      <Ionicons
        name={visible ? 'eye-off-outline' : 'eye-outline'}
        size={20}
        color={theme.colors.textMuted}
      />
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 12 },
  form: { padding: 18 },
  deliveryNote: {
    minHeight: 64,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 11,
    padding: 12,
    marginBottom: 16,
  },
  deliveryIcon: {
    width: 36,
    height: 36,
    borderRadius: 13,
    alignItems: 'center',
    justifyContent: 'center',
  },
  deliveryCopy: { flex: 1, minWidth: 0, gap: 2 },
  visibilityButton: {
    width: 44,
    height: 44,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  resendButton: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
    marginTop: 10,
  },
  resendText: { fontWeight: '700' },
  footer: { textAlign: 'center', paddingHorizontal: 18 },
});
