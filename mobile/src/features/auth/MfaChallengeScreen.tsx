import React, { useState } from 'react';
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { formatCountdown, MFA_CODE_LENGTH, secondsLeft } from '@/auth/mfaFlow';
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
import { MfaCodeError, MfaCodeInput, useCodeEntry } from './mfaCodeEntry';

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaChallenge'>;

/** Step 2 of sign-in for an enrolled user: the 6-digit code from the authenticator app. */
export default function MfaChallengeScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const { challengeToken, tenantId, email, expiresInSeconds, justEnrolled } = route.params;
  const completeMfa = useAuthStore((s) => s.completeMfa);
  const [code, setCode] = useState('');
  const { state, nowMs, run, edited } = useCodeEntry(expiresInSeconds, { justEnrolled });

  const finished = state.phase === 'locked' || state.phase === 'expired';
  const submitting = state.phase === 'submitting' || state.phase === 'succeeded';
  const canSubmit = state.phase === 'ready' && code.length === MFA_CODE_LENGTH;

  const submit = () => {
    if (!canSubmit) return;
    const entered = code;
    // Cleared up front so a rejected code never lingers and the next one can be autofilled.
    setCode('');
    void run(() => completeMfa(challengeToken, entered, tenantId));
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
          title={t('mfa.challengeTitle')}
          subtitle={t('mfa.challengeBody', { email })}
          onBack={() => navigation.goBack()}
          backLabel={t('mfa.backToSignIn')}
        />

        <View style={styles.content}>
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
            <View style={[styles.identityIcon, { backgroundColor: theme.colors.cyan + '18' }]}>
              <Ionicons name="shield-checkmark-outline" size={30} color={theme.colors.cyan} />
            </View>

            <View style={styles.inputGap} />
            <MfaCodeInput
              value={code}
              onChange={(value) => {
                edited();
                setCode(value);
              }}
              editable={!finished && !submitting}
              autoFocus
              onSubmit={submit}
              tone={{
                text: theme.colors.text,
                placeholder: theme.colors.textMuted,
                background: theme.colors.surfaceSoft,
                border: state.error ? theme.colors.danger : theme.colors.glassBorder,
              }}
            />

            <MfaCodeError state={state} color={theme.colors.danger} />
            {!finished ? (
              <Text style={[theme.typography.caption, styles.timer, { color: theme.colors.textMuted }]}>
                {t('mfa.expiresIn', { time: formatCountdown(secondsLeft(state, nowMs)) })}
              </Text>
            ) : null}

            {finished ? (
              <LiquidButton
                label={t('mfa.backToSignIn')}
                icon="arrow-back-outline"
                onPress={() => navigation.goBack()}
                style={styles.button}
              />
            ) : (
              <>
                <LiquidButton
                  label={t('mfa.verify')}
                  icon="log-in-outline"
                  onPress={submit}
                  loading={submitting}
                  disabled={!canSubmit}
                  style={styles.button}
                />
                <MotionPressable
                  accessibilityRole="button"
                  accessibilityLabel={t('mfa.backToSignIn')}
                  onPress={() => navigation.goBack()}
                  haptic="selection"
                  contentStyle={styles.secondary}
                >
                  <Text style={[theme.typography.caption, styles.secondaryText, { color: theme.colors.primary }]}>
                    {t('mfa.backToSignIn')}
                  </Text>
                </MotionPressable>
              </>
            )}
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
  inputGap: { height: 22 },
  timer: { textAlign: 'center', marginTop: 14 },
  button: { marginTop: 18 },
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
