import React, { useState } from 'react';
import {
  ActivityIndicator,
  I18nManager,
  KeyboardAvoidingView,
  Platform,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { LinearGradient } from 'expo-linear-gradient';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/auth/authStore';
import { formatCountdown, MFA_CODE_LENGTH, secondsLeft } from '@/auth/mfaFlow';
import { COLORS } from '@/config';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';
import { MfaCodeError, MfaCodeInput, useCodeEntry } from './mfaCodeEntry';

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaChallenge'>;

/** Step 2 of sign-in for an enrolled user: the 6-digit code from the authenticator app. */
export default function MfaChallengeScreen({ navigation, route }: Props) {
  const { t } = useTranslation();
  const { challengeToken, tenantId, email, expiresInSeconds } = route.params;
  const completeMfa = useAuthStore((s) => s.completeMfa);
  const [code, setCode] = useState('');
  const { state, nowMs, run, edited } = useCodeEntry(expiresInSeconds);

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
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : 'height'}>
      <LinearGradient colors={['#0B1020', '#0F1830', '#0B1020']} style={StyleSheet.absoluteFill} />
      <TouchableOpacity
        style={styles.back}
        onPress={() => navigation.goBack()}
        accessibilityRole="button"
        accessibilityLabel={t('mfa.backToSignIn')}
      >
        <Ionicons name={I18nManager.isRTL ? 'arrow-forward' : 'arrow-back'} size={22} color="#fff" />
      </TouchableOpacity>

      <View style={styles.card}>
        <View style={styles.iconCircle}>
          <Ionicons name="shield-checkmark-outline" size={34} color={COLORS.cyan} />
        </View>
        <Text style={styles.title}>{t('mfa.challengeTitle')}</Text>
        <Text style={styles.subtitle}>{t('mfa.challengeBody', { email })}</Text>

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
        />

        <MfaCodeError state={state} />
        {!finished ? (
          <Text style={styles.timer}>
            {t('mfa.expiresIn', { time: formatCountdown(secondsLeft(state, nowMs)) })}
          </Text>
        ) : null}

        {finished ? (
          <TouchableOpacity style={styles.button} onPress={() => navigation.goBack()} accessibilityRole="button">
            <Text style={styles.buttonText}>{t('mfa.backToSignIn')}</Text>
          </TouchableOpacity>
        ) : (
          <>
            <TouchableOpacity
              style={[styles.button, !canSubmit && styles.buttonDisabled]}
              onPress={submit}
              disabled={!canSubmit}
              accessibilityRole="button"
              accessibilityState={{ disabled: !canSubmit, busy: submitting }}
            >
              {submitting ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>{t('mfa.verify')}</Text>}
            </TouchableOpacity>
            <TouchableOpacity onPress={() => navigation.goBack()} style={styles.secondary} accessibilityRole="button">
              <Text style={styles.secondaryText}>{t('mfa.backToSignIn')}</Text>
            </TouchableOpacity>
          </>
        )}
      </View>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', padding: 24, backgroundColor: COLORS.navy },
  back: { position: 'absolute', top: 58, start: 22, zIndex: 1, padding: 8 },
  card: {
    borderRadius: 22,
    padding: 24,
    backgroundColor: 'rgba(255,255,255,0.055)',
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.1)',
  },
  iconCircle: {
    width: 64,
    height: 64,
    borderRadius: 32,
    alignSelf: 'center',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(94,235,255,0.1)',
    marginBottom: 18,
  },
  title: { color: '#fff', fontSize: 24, fontWeight: '800', textAlign: 'center' },
  subtitle: { color: 'rgba(255,255,255,0.62)', fontSize: 14, lineHeight: 21, textAlign: 'center', marginTop: 9 },
  inputGap: { height: 26 },
  timer: { color: 'rgba(255,255,255,0.45)', textAlign: 'center', marginTop: 14, fontSize: 12 },
  button: { backgroundColor: COLORS.blue, borderRadius: 14, alignItems: 'center', paddingVertical: 15, marginTop: 22 },
  buttonDisabled: { opacity: 0.45 },
  buttonText: { color: '#fff', fontSize: 15, fontWeight: '800' },
  secondary: { alignItems: 'center', marginTop: 18, paddingVertical: 6 },
  secondaryText: { color: COLORS.cyan, fontSize: 13, fontWeight: '600' },
});
