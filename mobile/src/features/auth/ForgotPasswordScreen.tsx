import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  ScrollView,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { authApi } from '@/api/adapters';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from '@/auth/publicAuthInput';
import { COLORS } from '@/config';
import { appStorage } from '@/storage';

/**
 * G1 deliberately stops at reset-link issuance. The authoritative credential
 * route is the HTTPS web link in the email; the native app does not claim
 * Universal/App Link ingestion until that association is independently proven.
 */
export default function ForgotPasswordScreen() {
  const navigation = useNavigation<any>();
  const [email, setEmail] = useState('');
  const [workspace, setWorkspace] = useState('');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    appStorage.get<string>('zayra_tenant_id')
      .then((value) => value && setWorkspace(normalizeWorkspace(value)))
      .catch(() => undefined);
  }, []);

  const requestReset = async () => {
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) return Alert.alert('Required', 'Please enter your work email');

    let normalizedWorkspace: string;
    try {
      normalizedWorkspace = requireWorkspace(workspace);
    } catch {
      return Alert.alert('Required', 'Please enter your workspace');
    }

    setLoading(true);
    try {
      await authApi.forgotPassword({ email: normalizedEmail, tenantSlug: normalizedWorkspace });
      setEmail('');
      Alert.alert(
        'Check Your Email',
        'If that account exists, we sent a secure password-reset link. Open the HTTPS link to continue.',
        [{ text: 'Back to Login', onPress: () => navigation.navigate('Login') }],
      );
    } catch (error: any) {
      Alert.alert(
        'Request Failed',
        error?.response?.data?.message || error?.message || 'We could not request a reset link. Try again later.',
      );
    } finally {
      setLoading(false);
    }
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.navy }}>
      <ScrollView
        contentContainerStyle={{ flexGrow: 1, padding: 24, paddingTop: 80 }}
        keyboardShouldPersistTaps="handled"
      >
        <TouchableOpacity onPress={() => navigation.goBack()} style={{ marginBottom: 32 }}>
          <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 14 }}>← Back to Login</Text>
        </TouchableOpacity>

        <View style={{ alignItems: 'center', marginBottom: 40 }}>
          <View style={{
            width: 64,
            height: 64,
            borderRadius: 16,
            backgroundColor: COLORS.blue,
            alignItems: 'center',
            justifyContent: 'center',
          }}>
            <Text style={{ color: '#fff', fontSize: 28, fontWeight: '800' }}>K</Text>
          </View>
          <Text style={{ color: '#fff', fontSize: 24, fontWeight: '800', marginTop: 14 }}>
            Reset Password
          </Text>
          <Text style={{
            color: 'rgba(255,255,255,0.6)',
            fontSize: 14,
            marginTop: 6,
            textAlign: 'center',
            lineHeight: 20,
          }}>
            Enter your workspace and work email. We will send a secure HTTPS reset link.
          </Text>
        </View>

        <View style={{ backgroundColor: 'rgba(255,255,255,0.07)', borderRadius: 20, padding: 20 }}>
          <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
            Workspace
          </Text>
          <TextInput
            value={workspace}
            onChangeText={setWorkspace}
            placeholder="e.g. acme-corp"
            placeholderTextColor="rgba(255,255,255,0.3)"
            autoCapitalize="none"
            autoCorrect={false}
            style={{
              backgroundColor: 'rgba(255,255,255,0.1)',
              borderRadius: 12,
              paddingHorizontal: 14,
              paddingVertical: 14,
              fontSize: 15,
              color: '#fff',
              marginBottom: 16,
            }}
          />

          <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
            Work Email
          </Text>
          <TextInput
            value={email}
            onChangeText={setEmail}
            placeholder="you@company.com"
            placeholderTextColor="rgba(255,255,255,0.3)"
            autoCapitalize="none"
            autoCorrect={false}
            keyboardType="email-address"
            style={{
              backgroundColor: 'rgba(255,255,255,0.1)',
              borderRadius: 12,
              paddingHorizontal: 14,
              paddingVertical: 14,
              fontSize: 15,
              color: '#fff',
              marginBottom: 20,
            }}
          />

          <TouchableOpacity
            onPress={requestReset}
            disabled={loading}
            style={{
              backgroundColor: loading ? 'rgba(47,107,255,0.5)' : COLORS.blue,
              borderRadius: 12,
              padding: 16,
              alignItems: 'center',
            }}
          >
            {loading
              ? <ActivityIndicator color="#fff" />
              : <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Send Reset Link</Text>}
          </TouchableOpacity>
        </View>
      </ScrollView>
    </View>
  );
}
