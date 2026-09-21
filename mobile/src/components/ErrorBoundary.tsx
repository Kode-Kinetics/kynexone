// ============================================================
// KynexOne Mobile — Root error boundary
// ============================================================
//
// React unmounts the whole tree when a render throws and nothing catches it,
// which on a device is a blank white screen with no way out but force-quitting.
// This boundary sits above the navigator, so any render exception in any screen
// lands here instead: the user sees what happened and can recover in place.

import React from 'react';
import { View, Text, TouchableOpacity, StyleSheet, ScrollView } from 'react-native';
import { COLORS } from '@/config';

interface Props {
  children: React.ReactNode;
  /** Called once per caught error — the hook for crash reporting when one is wired. */
  onError?: (error: Error, componentStack?: string | null) => void;
}

interface State {
  error: Error | null;
}

export class ErrorBoundary extends React.Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    // warn, not error: on debug native builds (Expo Go, dev clients) console.error
    // raises React Native's red-box over the fallback this boundary just rendered.
    // The error is handled here; crash reporting goes through onError.
    console.warn('[ErrorBoundary] Render failure caught:', error?.message, info.componentStack);
    this.props.onError?.(error, info.componentStack);
  }

  reset = () => this.setState({ error: null });

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;

    return (
      <View style={styles.container} testID="error-boundary-fallback">
        <View style={styles.card}>
          <Text style={styles.icon}>⚠️</Text>
          <Text style={styles.title}>Something went wrong</Text>
          <Text style={styles.body}>
            This screen hit an unexpected error. Your data is safe — try again, and if it keeps
            happening, contact your HR team.
          </Text>
          {__DEV__ && (
            <ScrollView style={styles.devBox}>
              <Text style={styles.devText}>{error.name}: {error.message}</Text>
            </ScrollView>
          )}
          <TouchableOpacity style={styles.button} onPress={this.reset} accessibilityRole="button">
            <Text style={styles.buttonText}>Try again</Text>
          </TouchableOpacity>
        </View>
      </View>
    );
  }
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: COLORS.navy,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
  },
  card: {
    width: '100%',
    backgroundColor: COLORS.white,
    borderRadius: 16,
    padding: 24,
    alignItems: 'center',
  },
  icon: { fontSize: 40, marginBottom: 12 },
  title: { fontSize: 20, fontWeight: '700', color: COLORS.text, marginBottom: 8 },
  body: { fontSize: 14, color: COLORS.textSecondary, textAlign: 'center', lineHeight: 20 },
  devBox: {
    maxHeight: 120,
    alignSelf: 'stretch',
    marginTop: 16,
    padding: 10,
    backgroundColor: '#FEF2F2',
    borderRadius: 8,
  },
  devText: { fontSize: 12, color: COLORS.error, fontFamily: 'Courier' },
  button: {
    marginTop: 20,
    backgroundColor: COLORS.blue,
    borderRadius: 12,
    paddingVertical: 12,
    paddingHorizontal: 32,
  },
  buttonText: { color: COLORS.white, fontSize: 15, fontWeight: '700' },
});
