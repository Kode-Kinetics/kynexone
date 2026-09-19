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
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { LinearGradient } from 'expo-linear-gradient';
import { authApi } from '@/api/services';
import { COLORS } from '@/config';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaEnrollment'>;

export default function MfaEnrollmentScreen({ navigation, route }: Props) {
  const { enrollmentToken, tenantId, email, expiresInSeconds, message } = route.params;
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
        if (active) setError(reason?.response?.data?.message ?? reason?.message ?? 'Unable to start MFA setup.');
      })
      .finally(() => active && setLoading(false));
    return () => {
      active = false;
    };
  }, [enrollmentToken, tenantId]);

  useEffect(() => {
    const timer = setInterval(() => setRemaining((value) => Math.max(0, value - 1)), 1000);
    return () => clearInterval(timer);
  }, []);

  const timeLabel = useMemo(() => {
    const minutes = Math.floor(remaining / 60);
    const seconds = remaining % 60;
    return `${minutes}:${String(seconds).padStart(2, '0')}`;
  }, [remaining]);

  const openAuthenticator = async () => {
    if (!provisioningUri) return;
    try {
      await Linking.openURL(provisioningUri);
    } catch {
      Alert.alert('Authenticator app', 'Open your authenticator app and enter the setup key shown below.');
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
        'Multi-factor authentication is now enabled. Sign in again to continue.',
        [
          {
            text: 'Continue',
            onPress: () => navigation.navigate('Login', { tenantId, email, enrollmentComplete: true }),
          },
        ]
      );
    } catch (reason: any) {
      setError(reason?.response?.data?.message ?? reason?.message ?? 'The code could not be verified.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : 'height'}>
      <LinearGradient colors={['#0B1020', '#0F1830', '#0B1020']} style={StyleSheet.absoluteFill} />
      <TouchableOpacity style={styles.back} onPress={() => navigation.goBack()} accessibilityLabel="Back to sign in">
        <Ionicons name="arrow-back" size={22} color="#fff" />
      </TouchableOpacity>

      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
        <View style={styles.card}>
          <View style={styles.iconCircle}>
            <Ionicons name="key-outline" size={34} color={COLORS.cyan} />
          </View>
          <Text style={styles.title}>Secure your account</Text>
          <Text style={styles.subtitle}>
            {message || 'Your organization requires multi-factor authentication.'}
          </Text>
          <Text style={styles.account}>{email}</Text>

          {loading ? (
            <ActivityIndicator color={COLORS.cyan} style={{ marginVertical: 30 }} />
          ) : error && !tempSecret ? (
            <View style={styles.errorBox}>
              <Text style={styles.error}>{error}</Text>
            </View>
          ) : (
            <>
              <Text style={styles.stepTitle}>1. Add KynexOne to your authenticator app</Text>
              <TouchableOpacity style={styles.authButton} onPress={openAuthenticator}>
                <Ionicons name="open-outline" size={18} color={COLORS.blue} />
                <Text style={styles.authButtonText}>Open Authenticator App</Text>
              </TouchableOpacity>

              <Text style={styles.helper}>
                If the app does not open automatically, add an account manually and enter this setup key:
              </Text>
              <View style={styles.secretBox}>
                <Text selectable style={styles.secret}>{tempSecret}</Text>
              </View>
              <Text style={styles.securityNote}>Keep this key private. KynexOne will never show it again.</Text>

              <Text style={styles.stepTitle}>2. Enter the 6-digit code</Text>
              <TextInput
                value={code}
                onChangeText={(value) => {
                  setError('');
                  setCode(value.replace(/\D/g, '').slice(0, 6));
                }}
                style={styles.codeInput}
                keyboardType="number-pad"
                textContentType="oneTimeCode"
                autoComplete="one-time-code"
                maxLength={6}
                editable={!submitting && remaining > 0}
                accessibilityLabel="Authentication code"
              />
              {error ? <Text style={styles.error}>{error}</Text> : null}
              <Text style={[styles.timer, remaining === 0 && styles.expired]}>
                {remaining > 0 ? `Setup expires in ${timeLabel}` : 'This setup challenge has expired. Return to sign in.'}
              </Text>

              <TouchableOpacity
                style={[styles.verifyButton, (code.length !== 6 || submitting || remaining === 0) && styles.disabled]}
                onPress={verify}
                disabled={code.length !== 6 || submitting || remaining === 0}
              >
                {submitting ? <ActivityIndicator color="#fff" /> : <Text style={styles.verifyText}>Enable MFA</Text>}
              </TouchableOpacity>
            </>
          )}
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.navy },
  scroll: { flexGrow: 1, justifyContent: 'center', padding: 24, paddingTop: 90, paddingBottom: 50 },
  back: { position: 'absolute', top: 58, left: 22, zIndex: 2, padding: 8 },
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
    marginBottom: 16,
  },
  title: { color: '#fff', fontSize: 24, fontWeight: '800', textAlign: 'center' },
  subtitle: { color: 'rgba(255,255,255,0.58)', fontSize: 14, lineHeight: 20, textAlign: 'center', marginTop: 8 },
  account: { color: COLORS.cyan, textAlign: 'center', fontSize: 13, fontWeight: '600', marginTop: 7 },
  stepTitle: { color: '#fff', fontSize: 14, fontWeight: '700', marginTop: 24, marginBottom: 10 },
  authButton: {
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
    gap: 8,
    backgroundColor: '#fff',
    borderRadius: 12,
    paddingVertical: 13,
  },
  authButtonText: { color: COLORS.blue, fontWeight: '700' },
  helper: { color: 'rgba(255,255,255,0.5)', fontSize: 12, lineHeight: 18, marginTop: 14 },
  secretBox: { backgroundColor: 'rgba(255,255,255,0.08)', borderRadius: 10, padding: 13, marginTop: 9 },
  secret: { color: '#fff', textAlign: 'center', fontSize: 15, fontWeight: '800', letterSpacing: 2 },
  securityNote: { color: 'rgba(255,255,255,0.36)', fontSize: 11, textAlign: 'center', marginTop: 8 },
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
    paddingVertical: 15,
    paddingLeft: 12,
  },
  timer: { color: 'rgba(255,255,255,0.45)', textAlign: 'center', marginTop: 12, fontSize: 12 },
  expired: { color: '#FCA5A5' },
  verifyButton: { backgroundColor: COLORS.blue, borderRadius: 14, alignItems: 'center', paddingVertical: 15, marginTop: 20 },
  verifyText: { color: '#fff', fontSize: 15, fontWeight: '800' },
  disabled: { opacity: 0.45 },
  errorBox: { backgroundColor: 'rgba(239,68,68,0.12)', borderRadius: 12, padding: 14, marginTop: 22 },
  error: { color: '#FCA5A5', textAlign: 'center', marginTop: 10, fontSize: 13 },
});
