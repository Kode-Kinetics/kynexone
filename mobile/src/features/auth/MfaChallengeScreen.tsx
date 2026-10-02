import React, { useEffect, useMemo, useState } from 'react';
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useAuthStore } from '@/auth/authStore';
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

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaChallenge'>;

export default function MfaChallengeScreen({ navigation, route }: Props) {
  const { challengeToken, tenantId, email, expiresInSeconds } = route.params;
  const { completeMfa, isLoading, error, clearError } = useAuthStore();
  const { theme } = useTheme();
  const [code, setCode] = useState('');
  const [remaining, setRemaining] = useState(Math.max(0, expiresInSeconds || 300));

  useEffect(() => {
    const timer = setInterval(
      () => setRemaining((value) => Math.max(0, value - 1)),
      1000,
    );
    return () => clearInterval(timer);
  }, []);

  useEffect(() => () => clearError(), [clearError]);

  const timeLabel = useMemo(() => {
    const minutes = Math.floor(remaining / 60);
    const seconds = remaining % 60;
    return minutes + ':' + String(seconds).padStart(2, '0');
  }, [remaining]);

  const submit = async () => {
    if (code.length !== 6 || remaining <= 0) return;
    try {
      await completeMfa(challengeToken, code, tenantId);
    } catch {
      // The auth store exposes the user-facing error.
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
          eyebrow="Multi-factor authentication"
          title="Verify it’s you"
          subtitle="Enter the current 6-digit code from your authenticator app."
          onBack={() => navigation.goBack()}
          backLabel="Back to sign in"
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
            <View style={[styles.identityIcon, { backgroundColor: theme.colors.cyan + '18' }]}>
              <Ionicons name="shield-checkmark-outline" size={30} color={theme.colors.cyan} />
            </View>

            <Text style={[theme.typography.bodyStrong, styles.email, { color: theme.colors.text }]}>
              {email}
            </Text>
            <Text style={[theme.typography.caption, styles.codeLabel, { color: theme.colors.textSecondary }]}>
              Authentication code
            </Text>

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
                  clearError();
                  setCode(value.replace(/\D/g, '').slice(0, 6));
                }}
                style={[styles.codeInput, { color: theme.colors.text }]}
                keyboardType="number-pad"
                textContentType="oneTimeCode"
                autoComplete="one-time-code"
                maxLength={6}
                autoFocus
                editable={!isLoading && !expired}
                accessibilityLabel="Authentication code"
                selectionColor={theme.colors.primary}
              />
            </View>

            {error ? (
              <View
                accessibilityRole="alert"
                accessibilityLiveRegion="assertive"
                style={[styles.errorBox, { backgroundColor: theme.colors.danger + '12' }]}
              >
                <Ionicons name="alert-circle-outline" size={17} color={theme.colors.danger} />
                <Text style={[theme.typography.caption, styles.errorText, { color: theme.colors.danger }]}>
                  {error}
                </Text>
              </View>
            ) : null}

            <Text
              style={[
                theme.typography.caption,
                styles.timer,
                { color: expired ? theme.colors.danger : theme.colors.textMuted },
              ]}
            >
              {expired ? 'This challenge has expired. Return to sign in.' : 'Code expires in ' + timeLabel}
            </Text>

            <LiquidButton
              label="Verify and sign in"
              icon="log-in-outline"
              onPress={() => void submit()}
              loading={isLoading}
              disabled={code.length !== 6 || isLoading || expired}
            />

            <MotionPressable
              accessibilityRole="button"
              accessibilityLabel="Use a different account"
              onPress={() => navigation.goBack()}
              haptic="selection"
              contentStyle={styles.secondary}
            >
              <Ionicons name="people-outline" size={17} color={theme.colors.primary} />
              <Text style={[theme.typography.caption, styles.secondaryText, { color: theme.colors.primary }]}>
                Use a different account
              </Text>
            </MotionPressable>
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
  identityIcon: {
    width: 58,
    height: 58,
    borderRadius: 21,
    alignSelf: 'center',
    alignItems: 'center',
    justifyContent: 'center',
  },
  email: { textAlign: 'center', marginTop: 12 },
  codeLabel: { textAlign: 'center', marginTop: 24, marginBottom: 8, fontWeight: '700' },
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
  secondary: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
    marginTop: 10,
  },
  secondaryText: { fontWeight: '700' },
});
