import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  KeyboardAvoidingView,
  Linking,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { authApi } from '@/api/services';
import {
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaEnrollment'>;

export default function MfaEnrollmentScreen({ navigation, route }: Props) {
  const { enrollmentToken, tenantId, email, expiresInSeconds, message } = route.params;
  const { theme } = useTheme();
  const [provisioningUri, setProvisioningUri] = useState('');
  const [tempSecret, setTempSecret] = useState('');
  const [code, setCode] = useState('');
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [remaining, setRemaining] = useState(Math.max(0, expiresInSeconds || 600));

  useEffect(() => {
    let active = true;
    authApi
      .startMfaEnrollment(enrollmentToken, tenantId)
      .then((result) => {
        if (!active) return;
        setProvisioningUri(result.provisioningUri);
        setTempSecret(result.tempSecret);
      })
      .catch((reason: any) => {
        if (active) {
          setError(reason?.response?.data?.message ?? reason?.message ?? 'Unable to start MFA setup.');
        }
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [enrollmentToken, tenantId]);

  useEffect(() => {
    const timer = setInterval(
      () => setRemaining((value) => Math.max(0, value - 1)),
      1000,
    );
    return () => clearInterval(timer);
  }, []);

  const timeLabel = useMemo(() => {
    const minutes = Math.floor(remaining / 60);
    const seconds = remaining % 60;
    return minutes + ':' + String(seconds).padStart(2, '0');
  }, [remaining]);

  const openAuthenticator = async () => {
    if (!provisioningUri) return;
    try {
      await Linking.openURL(provisioningUri);
    } catch {
      Alert.alert(
        'Authenticator app',
        'Open your authenticator app and enter the setup key shown below.',
      );
    }
  };

  const verify = async () => {
    if (code.length !== 6 || !tempSecret || remaining <= 0) return;
    setSubmitting(true);
    setError('');
    try {
      await authApi.verifyMfaEnrollment(enrollmentToken, tempSecret, code, tenantId);
      Alert.alert(
        'Security setup complete',
        'Multi-factor authentication is enabled. Sign in again to continue.',
        [{
          text: 'Continue',
          onPress: () => navigation.navigate('Login', {
            tenantId,
            email,
            enrollmentComplete: true,
          }),
        }],
      );
    } catch (reason: any) {
      setError(reason?.response?.data?.message ?? reason?.message ?? 'The code could not be verified.');
    } finally {
      setSubmitting(false);
    }
  };

  const expired = remaining <= 0;

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
          eyebrow="Account security"
          title="Secure your account"
          subtitle={message || 'Your organization requires multi-factor authentication.'}
          onBack={() => navigation.goBack()}
          backLabel="Back to sign in"
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
            <View style={[styles.identity, { backgroundColor: theme.colors.cyan + '18' }]}>
              <Ionicons name="key-outline" size={29} color={theme.colors.cyan} />
            </View>
            <Text style={[theme.typography.bodyStrong, styles.account, { color: theme.colors.text }]}>
              {email}
            </Text>

            {loading ? (
              <View
                accessibilityRole="progressbar"
                accessibilityLabel="Preparing secure setup"
                accessibilityLiveRegion="polite"
                style={styles.loadingState}
              >
                <ActivityIndicator color={theme.colors.primary} />
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                  Preparing secure setup…
                </Text>
              </View>
            ) : error && !tempSecret ? (
              <ErrorBox message={error} />
            ) : (
              <>
                <StepLabel number="1" title="Add KynexOne to your authenticator" />
                <MotionPressable
                  accessibilityRole="button"
                  accessibilityLabel="Open authenticator app"
                  onPress={() => void openAuthenticator()}
                  haptic="selection"
                  contentStyle={[
                    styles.authenticatorButton,
                    {
                      backgroundColor: theme.colors.surfaceSoft,
                      borderColor: theme.colors.glassBorder,
                    },
                  ]}
                >
                  <Ionicons name="open-outline" size={19} color={theme.colors.primary} />
                  <Text style={[theme.typography.bodyStrong, { color: theme.colors.primary }]}>
                    Open authenticator app
                  </Text>
                </MotionPressable>

                <Text style={[theme.typography.caption, styles.helper, { color: theme.colors.textSecondary }]}>
                  If the app does not open, add an account manually with this one-time setup key.
                </Text>

                <GlassSurface
                  elevated={false}
                  radius={16}
                  contentStyle={styles.secretBox}
                  tintColor={theme.isDark ? 'rgba(94,235,255,0.08)' : 'rgba(255,255,255,0.48)'}
                >
                  <Text
                    selectable
                    accessibilityLabel={`One-time setup key: ${tempSecret}`}
                    style={[styles.secret, { color: theme.colors.text }]}
                  >
                    {tempSecret}
                  </Text>
                </GlassSurface>
                <Text style={[theme.typography.micro, styles.securityNote, { color: theme.colors.textMuted }]}>
                  Keep this setup key private. It will not be shown again after enrollment.
                </Text>

                <StepLabel number="2" title="Enter the 6-digit code" />
                <View
                  style={[
                    styles.codeFrame,
                    {
                      borderColor: error ? theme.colors.danger : theme.colors.glassBorder,
                      backgroundColor: theme.colors.surfaceSoft,
                    },
                  ]}
                >
                  <TextInput
                    value={code}
                    onChangeText={(value) => {
                      setError('');
                      setCode(value.replace(/\D/g, '').slice(0, 6));
                    }}
                    style={[styles.codeInput, { color: theme.colors.text }]}
                    keyboardType="number-pad"
                    textContentType="oneTimeCode"
                    autoComplete="one-time-code"
                    maxLength={6}
                    editable={!submitting && !expired}
                    accessibilityLabel="Authentication code"
                    selectionColor={theme.colors.primary}
                  />
                </View>

                {error ? <ErrorBox message={error} /> : null}
                <Text
                  style={[
                    theme.typography.caption,
                    styles.timer,
                    { color: expired ? theme.colors.danger : theme.colors.textMuted },
                  ]}
                >
                  {expired ? 'This setup challenge has expired. Return to sign in.' : 'Setup expires in ' + timeLabel}
                </Text>

                <LiquidButton
                  label="Enable MFA"
                  icon="shield-checkmark-outline"
                  onPress={() => void verify()}
                  loading={submitting}
                  disabled={code.length !== 6 || submitting || expired}
                />
              </>
            )}
          </GlassSurface>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function StepLabel({ number, title }: { number: string; title: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.stepRow}>
      <View style={[styles.stepNumber, { backgroundColor: theme.colors.primary + '18' }]}>
        <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '800' }]}>
          {number}
        </Text>
      </View>
      <Text style={[theme.typography.bodyStrong, styles.stepTitle, { color: theme.colors.text }]}>
        {title}
      </Text>
    </View>
  );
}

function ErrorBox({ message }: { message: string }) {
  const { theme } = useTheme();
  return (
    <View
      accessibilityRole="alert"
      accessibilityLiveRegion="assertive"
      style={[styles.errorBox, { backgroundColor: theme.colors.danger + '12' }]}
    >
      <Ionicons name="alert-circle-outline" size={17} color={theme.colors.danger} />
      <Text style={[theme.typography.caption, styles.errorText, { color: theme.colors.danger }]}>
        {message}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12 },
  card: { padding: 20 },
  identity: {
    width: 56,
    height: 56,
    borderRadius: 20,
    alignSelf: 'center',
    alignItems: 'center',
    justifyContent: 'center',
  },
  account: { textAlign: 'center', marginTop: 10 },
  loadingState: { minHeight: 150, alignItems: 'center', justifyContent: 'center', gap: 12 },
  stepRow: { flexDirection: 'row', alignItems: 'center', gap: 9, marginTop: 24, marginBottom: 10 },
  stepNumber: {
    width: 28,
    height: 28,
    borderRadius: 10,
    alignItems: 'center',
    justifyContent: 'center',
  },
  stepTitle: { flex: 1, minWidth: 0 },
  authenticatorButton: {
    minHeight: 52,
    borderRadius: 16,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    paddingHorizontal: 14,
  },
  helper: { lineHeight: 18 },
  secretBox: { minHeight: 58, alignItems: 'center', justifyContent: 'center', padding: 12, marginTop: 10 },
  secret: { textAlign: 'center', fontSize: 15, lineHeight: 21, fontWeight: '800', letterSpacing: 1.5 },
  securityNote: { textAlign: 'center', marginTop: 8 },
  codeFrame: { borderWidth: 1, borderRadius: 18, overflow: 'hidden' },
  codeInput: {
    minHeight: 64,
    fontSize: 28,
    lineHeight: 34,
    fontWeight: '800',
    letterSpacing: 8,
    textAlign: 'center',
    paddingHorizontal: 12,
  },
  errorBox: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    gap: 7,
    borderRadius: 14,
    padding: 11,
    marginTop: 12,
  },
  errorText: { flex: 1, lineHeight: 18 },
  timer: { textAlign: 'center', marginVertical: 14 },
});
