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
import { authApi } from '@/api/adapters';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from '@/auth/publicAuthInput';
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
 */
export default function ForgotPasswordScreen() {
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const [email, setEmail] = useState('');
  const [workspace, setWorkspace] = useState('');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    appStorage
      .get<string>('zayra_tenant_id')
      .then((value) => value && setWorkspace(normalizeWorkspace(value)))
      .catch(() => undefined);
  }, []);

  const requestReset = async () => {
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) return Alert.alert('Work email required', 'Enter your work email.');

    let normalizedWorkspace: string;
    try {
      normalizedWorkspace = requireWorkspace(workspace);
    } catch {
      return Alert.alert('Workspace required', 'Enter your workspace.');
    }

    setLoading(true);
    try {
      await authApi.forgotPassword({ email: normalizedEmail, tenantSlug: normalizedWorkspace });
      setEmail('');
      Alert.alert(
        'Check your email',
        'If that account exists, we sent a secure password-reset link. Open the HTTPS link to continue.',
        [{ text: 'Back to sign in', onPress: () => navigation.navigate('Login') }],
      );
    } catch (error: any) {
      Alert.alert(
        'Request failed',
        error?.response?.data?.message || error?.message || 'We could not request a reset link. Try again later.',
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
          title="Reset your password"
          subtitle="Enter your workspace and work email. We will send a secure HTTPS reset link."
          onBack={() => navigation.goBack()}
          backLabel="Back to sign in"
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            <GlassTextField
              label="Workspace"
              icon="business-outline"
              value={workspace}
              onChangeText={setWorkspace}
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
              label="Send reset link"
              icon="mail-unread-outline"
              onPress={() => void requestReset()}
              loading={loading}
              disabled={loading}
            />
          </GlassSurface>

          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            For your privacy, KynexOne does not confirm whether an email address exists.
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
});
