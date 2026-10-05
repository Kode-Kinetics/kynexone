import React, { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { StyleSheet, Text, TextInput } from 'react-native';
import { useTranslation } from 'react-i18next';
import {
  attemptsLeft,
  classifyMfaFailure,
  codeEntryReducer,
  initialCodeEntry,
  normalizeCode,
  type CodeEntryEvent,
  type CodeEntryState,
} from '@/auth/mfaFlow';

/**
 * Drives one code request (sign-in challenge or first-code enrolment check)
 * through mfaFlow.codeEntryReducer, with a 1 s clock for the expiry countdown.
 */
export function useCodeEntry(expiresInSeconds: number) {
  const [state, dispatch] = useReducer(codeEntryReducer, undefined, () =>
    initialCodeEntry(expiresInSeconds, Date.now())
  );
  const [nowMs, setNowMs] = useState(() => Date.now());
  const stateRef = useRef<CodeEntryState>(state);
  // Set synchronously so a double tap cannot send the same code twice.
  const inFlight = useRef(false);

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  useEffect(() => {
    const timer = setInterval(() => {
      const now = Date.now();
      setNowMs(now);
      dispatch({ type: 'tick', nowMs: now });
    }, 1000);
    return () => clearInterval(timer);
  }, []);

  /** Runs the request only if the machine accepts a submit; failures are classified, never echoed. */
  const run = useCallback(async (request: () => Promise<void>) => {
    if (inFlight.current) return;
    const submit: CodeEntryEvent = { type: 'submit', nowMs: Date.now() };
    const accepted = codeEntryReducer(stateRef.current, submit).phase === 'submitting';
    dispatch(submit);
    if (!accepted) return;
    inFlight.current = true;
    try {
      await request();
      dispatch({ type: 'succeeded' });
    } catch (error: unknown) {
      dispatch({ type: 'failed', failure: classifyMfaFailure(error), nowMs: Date.now() });
    } finally {
      inFlight.current = false;
    }
  }, []);

  const edited = useCallback(() => dispatch({ type: 'edited' }), []);

  return { state, nowMs, run, edited };
}

export function MfaCodeInput({
  value,
  onChange,
  editable,
  autoFocus,
  onSubmit,
}: {
  value: string;
  onChange: (code: string) => void;
  editable: boolean;
  autoFocus?: boolean;
  onSubmit?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <TextInput
      value={value}
      onChangeText={(text) => onChange(normalizeCode(text))}
      style={styles.codeInput}
      keyboardType="number-pad"
      inputMode="numeric"
      textContentType="oneTimeCode"
      autoComplete="one-time-code"
      importantForAutofill="yes"
      maxLength={6}
      autoFocus={autoFocus}
      editable={editable}
      returnKeyType="done"
      onSubmitEditing={onSubmit}
      autoCorrect={false}
      secureTextEntry={false}
      accessibilityLabel={t('mfa.codeLabel')}
      placeholder="••••••"
      placeholderTextColor="rgba(255,255,255,0.25)"
    />
  );
}

/** Plain-language error for the current state, with attempts left after a wrong code. */
export function MfaCodeError({ state }: { state: CodeEntryState }) {
  const { t } = useTranslation();
  if (!state.error) return null;
  return (
    <Text style={styles.error} accessibilityLiveRegion="polite" accessibilityRole="alert">
      {t(`mfa.errors.${state.error}`)}
      {state.error === 'wrongCode' ? `\n${t('mfa.attemptsLeft', { count: attemptsLeft(state) })}` : ''}
    </Text>
  );
}

const styles = StyleSheet.create({
  codeInput: {
    backgroundColor: 'rgba(255,255,255,0.08)',
    borderRadius: 14,
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.14)',
    color: '#fff',
    fontSize: 30,
    fontWeight: '800',
    letterSpacing: 12,
    textAlign: 'center',
    // Digits read left-to-right in Arabic too.
    writingDirection: 'ltr',
    paddingVertical: 15,
    paddingStart: 12,
  },
  error: { color: '#FCA5A5', textAlign: 'center', marginTop: 12, fontSize: 13, lineHeight: 19 },
});
