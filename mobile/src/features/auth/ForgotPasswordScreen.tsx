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
import { useNavigation } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { authApi } from '@/api/adapters';
import { authFailure } from '@/auth/authStore';
import { normalizeEmail, normalizeWorkspace } from '@/auth/publicAuthInput';
import { appStorage } from '@/storage';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

/**
 * G1 deliberately stops at reset-link issuance. The authoritative credential
 * route is the HTTPS web link in the email; the native app does not claim
 * Universal/App Link ingestion until that association is independently proven.
 *
 * Like sign-in, the company ID is asked for only when the server answers `workspace_required`
 * (it then never sends mail). When that happens, or the reply says no email can be sent, the
 * screen points to the route that always works: a new welcome code from HR.
 */
export default function ForgotPasswordScreen() {
  const navigation = useNavigation<any>();
  const { t } = useTranslation();
  const { theme } = useTheme();
  const [email, setEmail] = useState('');
  const [workspace, setWorkspace] = useState('');
  const [showWorkspace, setShowWorkspace] = useState(false);
  const [message, setMessage] = useState('');
  const [askHr, setAskHr] = useState(false);
  const [loading, setLoading] = useState(false);

  // Only a pre-fill for the day the server asks for it; not sent while the field is hidden.
  useEffect(() => {
    appStorage
      .get<string>('zayra_tenant_id')
      .then((value) => value && setWorkspace(normalizeWorkspace(value)))
      .catch(() => undefined);
  }, []);

  const requestReset = async () => {
    setMessage('');
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) return setMessage(t('signin.emailRequired'));
    const companyId = showWorkspace ? normalizeWorkspace(workspace) : '';
    if (showWorkspace && !companyId) return setMessage(t('signin.companyIdRequired'));

    setLoading(true);
    try {
      const result = await authApi.forgotPassword({ email: normalizedEmail, tenantSlug: companyId || undefined });
      if (result?.emailDelivery === false) {
        setAskHr(true);
        return;
      }
      setEmail('');
      Alert.alert(t('signin.forgot.sentTitle'), t('signin.forgot.sentBody'), [
        { text: t('mfa.backToSignIn'), onPress: () => navigation.navigate('Login') },
      ]);
    } catch (error: unknown) {
      const { status, code } = authFailure(error);
      if (status === 400 && code === 'workspace_required') {
        setShowWorkspace(true);
        setAskHr(true);
        setMessage(t('signin.companyIdNeeded'));
        return;
      }
      if (status === 429) return setMessage(t('signin.errors.tooManyTries'));
      // Never the server's own text: it is English and may not match the reader's language.
      Alert.alert(t('signin.forgot.failedTitle'), status === undefined ? t('signin.errors.network') : t('signin.forgot.failedBody'));
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
          eyebrow={t('signin.forgot.eyebrow')}
          title={t('signin.forgot.title')}
          subtitle={t('signin.forgot.subtitle')}
          onBack={() => navigation.goBack()}
          backLabel={t('mfa.backToSignIn')}
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            <GlassTextField
              label={t('signin.workEmail')}
              icon="mail-outline"
              value={email}
              onChangeText={setEmail}
              placeholder={t('signin.emailPlaceholder')}
              autoCapitalize="none"
              autoCorrect={false}
              keyboardType="email-address"
              autoComplete="email"
              textContentType="emailAddress"
            />
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
                autoFocus
                style={{ textAlign: 'left' }}
              />
            ) : null}
            {message ? (
              <Text accessibilityRole="alert" style={[theme.typography.caption, styles.message, { color: theme.colors.text }]}>
                {message}
              </Text>
            ) : null}
            <LiquidButton
              label={t('signin.forgot.send')}
              icon="mail-unread-outline"
              onPress={() => void requestReset()}
              loading={loading}
              disabled={loading}
            />
          </GlassSurface>

          {askHr ? (
            <Text accessibilityLiveRegion="polite" testID="forgot-ask-hr"
              style={[theme.typography.caption, styles.footer, { color: theme.colors.text }]}>
              {t('signin.noEmail')}
            </Text>
          ) : null}
          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            {t('signin.forgot.privacy')}
          </Text>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 12 },
  form: { padding: 18 },
  footer: { textAlign: 'center', paddingHorizontal: 18 },
  message: { marginTop: 4, marginBottom: 8, lineHeight: 18 },
});
