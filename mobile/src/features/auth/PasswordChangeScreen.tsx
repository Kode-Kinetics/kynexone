import React, { useRef, useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import { authApi } from '@/api/adapters';
import { useAuthStore } from '@/auth/authStore';
import {
  afterPasswordSaved,
  classifyResetFailure,
  validateNewPassword,
  type PasswordChangeFieldError,
} from '@/auth/passwordChange';
import { GlassSurface, GlassTextField, LiquidBackdrop, LiquidButton, ScreenHero } from '@/components/ui';
import type { AuthStackParamList } from '@/navigation/authTypes';
import { useTheme } from '@/theme/ThemeProvider';

type Props = NativeStackScreenProps<AuthStackParamList, 'PasswordChange'>;

/**
 * Sign-in step for a user whose administrator set a temporary password: choose a new one, then
 * sign in again with it. The reset token and passwords stay in component state / navigation params
 * (memory); nothing is stored or logged.
 */
export default function PasswordChangeScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const { resetToken, tenantSlug, tenantId, email } = route.params;
  const login = useAuthStore((s) => s.login);
  const clearError = useAuthStore((s) => s.clearError);
  const [newPassword, setNewPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [fieldError, setFieldError] = useState<PasswordChangeFieldError | null>(null);
  const [serverError, setServerError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);

  const backToSignIn = (notice: 'passwordSaved' | 'passwordChangeExpired') => {
    navigation.navigate('Login', { email, tenantId: tenantId || undefined, notice });
  };

  const submit = async () => {
    if (inFlight.current) return;
    setServerError(null);
    const invalid = validateNewPassword(newPassword, confirm);
    setFieldError(invalid);
    if (invalid) return;
    inFlight.current = true;
    setBusy(true);
    try {
      await authApi.resetPassword({ token: resetToken, newPassword, tenantSlug });
    } catch (error) {
      inFlight.current = false;
      setBusy(false);
      const failure = classifyResetFailure(error);
      if (failure.kind === 'expired') return backToSignIn('passwordChangeExpired');
      if (failure.kind === 'rejected') return setServerError(failure.message ?? t('mfa.passwordChange.rejected'));
      return setServerError(t(`mfa.passwordChange.${failure.reason}`));
    }
    // The token is spent from here on: never leave the person on this form.
    try {
      const outcome = await login(email, newPassword, tenantId || tenantSlug || undefined);
      if (afterPasswordSaved({ ok: true, kind: outcome.kind }) === 'signInWithNotice') {
        backToSignIn('passwordSaved');
      } else if (outcome.kind === 'mfaChallenge') {
        navigation.navigate('MfaChallenge', outcome);
      } else if (outcome.kind === 'mfaEnrollment') {
        navigation.navigate('MfaEnrollment', outcome);
      }
      // 'authenticated': the store has finished sign-in and the root navigator switches to the app.
    } catch {
      clearError();
      backToSignIn('passwordSaved');
    }
  };

  const fieldMessage = fieldError ? t(`mfa.passwordChange.${fieldError}`) : undefined;

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
          title={t('mfa.passwordChange.title')}
          subtitle={t('mfa.passwordChange.body')}
          onBack={() => navigation.goBack()}
          backLabel={t('mfa.backToSignIn')}
        />
        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
            <GlassTextField
              label={t('mfa.passwordChange.newLabel')}
              icon="lock-closed-outline"
              value={newPassword}
              onChangeText={(v: string) => { setNewPassword(v); setFieldError(null); setServerError(null); }}
              secureTextEntry
              autoCapitalize="none"
              autoCorrect={false}
              autoComplete="new-password"
              textContentType="newPassword"
              editable={!busy}
              error={fieldError === 'tooShort' ? fieldMessage : undefined}
              testID="password-change-new"
            />
            <View style={styles.gap} />
            <GlassTextField
              label={t('mfa.passwordChange.confirmLabel')}
              icon="lock-closed-outline"
              value={confirm}
              onChangeText={(v: string) => { setConfirm(v); setFieldError(null); setServerError(null); }}
              secureTextEntry
              autoCapitalize="none"
              autoCorrect={false}
              autoComplete="new-password"
              textContentType="newPassword"
              editable={!busy}
              returnKeyType="done"
              onSubmitEditing={submit}
              error={fieldError === 'mismatch' ? fieldMessage : undefined}
              testID="password-change-confirm"
            />
            {serverError ? (
              <View accessibilityRole="alert" accessibilityLiveRegion="assertive" style={styles.errorRow}>
                <Ionicons accessible={false} name="alert-circle-outline" size={18} color={theme.colors.danger} />
                <Text style={[theme.typography.footnote, styles.errorText, { color: theme.colors.danger }]}>
                  {serverError}
                </Text>
              </View>
            ) : null}
            <LiquidButton
              label={t('mfa.passwordChange.submit')}
              icon="log-in-outline"
              onPress={submit}
              loading={busy}
              disabled={busy || !newPassword || !confirm}
              style={styles.button}
            />
          </GlassSurface>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12 },
  card: { padding: 20 },
  gap: { height: 14 },
  errorRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 8, marginTop: 14 },
  errorText: { flex: 1 },
  button: { marginTop: 18 },
});
