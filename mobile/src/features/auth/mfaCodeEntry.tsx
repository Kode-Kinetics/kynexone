import React, { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { StyleSheet, Text, TextInput } from 'react-native';
import { useTranslation } from 'react-i18next';
import { useTheme } from '@/theme/ThemeProvider';
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
export function useCodeEntry(expiresInSeconds: number, options: { justEnrolled?: boolean } = {}) {
  const [state, dispatch] = useReducer(codeEntryReducer, undefined, () =>
    initialCodeEntry(expiresInSeconds, Date.now(), options)
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
  tone,
}: {
  value: string;
  onChange: (code: string) => void;
  editable: boolean;
  autoFocus?: boolean;
  onSubmit?: () => void;
  /** Colours for a themed (light or dark) surface; the default suits the dark sign-in sheets. */
  tone?: { text: string; placeholder: string; background: string; border: string };
}) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const colors = tone ?? {
    text: theme.colors.text,
    placeholder: theme.colors.textMuted,
    background: theme.colors.surfaceInset,
    border: theme.colors.hairline,
  };
  return (
    <TextInput
      value={value}
      onChangeText={(text) => onChange(normalizeCode(text))}
      style={[
        styles.codeInput,
        { color: colors.text, backgroundColor: colors.background, borderColor: colors.border },
      ]}
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
      placeholderTextColor={colors.placeholder}
    />
  );
}

/** Plain-language error for the current state, with attempts left after a wrong code. */
export function MfaCodeError({ state, color }: { state: CodeEntryState; color?: string }) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  if (!state.error) return null;
  return (
    <Text style={[styles.error, { color: color ?? theme.colors.status.danger.fg }]} accessibilityLiveRegion="polite" accessibilityRole="alert">
      {t(`mfa.errors.${state.error}`)}
      {state.error === 'wrongCode' || state.error === 'notAccepted' || state.error === 'waitForNextCode' ? `\n${t('mfa.attemptsLeft', { count: attemptsLeft(state) })}` : ''}
    </Text>
  );
}

const styles = StyleSheet.create({
  codeInput: {
    borderRadius: 14,
    borderWidth: 1,
    fontSize: 30,
    fontWeight: '800',
    letterSpacing: 12,
    textAlign: 'center',
    // Digits read left-to-right in Arabic too.
    writingDirection: 'ltr',
    paddingVertical: 15,
    paddingStart: 12,
  },
  error: { textAlign: 'center', marginTop: 12, fontSize: 13, lineHeight: 19 },
});
