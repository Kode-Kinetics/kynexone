import React, { useEffect, useState } from 'react';
import {
  View, Text, TextInput, TouchableOpacity,
  ActivityIndicator, Alert, ScrollView,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { authApi } from '@/api/adapters';
import { COLORS } from '@/config';
import { appStorage } from '@/storage';

// Backend ResetPasswordRequest/AcceptInvitationRequest enforce MinLength(10).
const MIN_PASSWORD_LENGTH = 10;

export default function ForgotPasswordScreen() {
  const navigation = useNavigation<any>();
  const [step, setStep] = useState<'email' | 'reset'>('email');
  const [email, setEmail] = useState('');
  const [tenantSlug, setTenantSlug] = useState('');

  // The reset is tenant-scoped; reuse the Company ID remembered from the last sign-in.
  useEffect(() => {
    appStorage.get<string>('zayra_tenant_id').then((t) => t && setTenantSlug(t)).catch(() => undefined);
  }, []);
  const [token, setToken] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [showPw, setShowPw] = useState(false);

  const requestReset = async () => {
    if (!email.trim()) return Alert.alert('Required', 'Please enter your work email');
    if (!tenantSlug.trim()) return Alert.alert('Required', 'Please enter your Company ID');
    setLoading(true);
    try {
      await authApi.forgotPassword({ email: email.trim(), tenantSlug: tenantSlug.trim() });
      setStep('reset');
      Alert.alert('Sent', 'If this account exists, a reset link/code has been sent to your registered email.');
    } catch (e: any) {
      Alert.alert('Error', e?.response?.data?.message || e.message || 'Failed to send reset request');
    } finally {
      setLoading(false);
    }
  };

  const submitReset = async () => {
    if (!token.trim()) return Alert.alert('Required', 'Please enter the reset code');
    if (newPassword.length < MIN_PASSWORD_LENGTH)
      return Alert.alert('Weak Password', `Password must be at least ${MIN_PASSWORD_LENGTH} characters`);
    if (newPassword !== confirmPassword) return Alert.alert('Mismatch', 'Passwords do not match');
    setLoading(true);
    try {
      await authApi.resetPassword({ email: email.trim(), token: token.trim(), newPassword, tenantSlug: tenantSlug.trim() });
      Alert.alert(
        'Password Reset',
        'Your password has been reset successfully. Please log in with your new password.',
        [{ text: 'Login', onPress: () => navigation.navigate('Login') }]
      );
    } catch (e: any) {
      Alert.alert('Error', e?.response?.data?.message || 'Failed to reset password. The code may have expired.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.navy }}>
      <ScrollView contentContainerStyle={{ flexGrow: 1, padding: 24, paddingTop: 80 }} keyboardShouldPersistTaps="handled">
        {/* Back */}
        <TouchableOpacity onPress={() => navigation.goBack()} style={{ marginBottom: 32 }}>
          <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 14 }}>← Back to Login</Text>
        </TouchableOpacity>

        {/* Logo */}
        <View style={{ alignItems: 'center', marginBottom: 40 }}>
          <View style={{
            width: 64, height: 64, borderRadius: 16, backgroundColor: COLORS.blue,
            alignItems: 'center', justifyContent: 'center',
          }}>
            <Text style={{ color: '#fff', fontSize: 28, fontWeight: '800' }}>K</Text>
          </View>
          <Text style={{ color: '#fff', fontSize: 24, fontWeight: '800', marginTop: 14 }}>
            {step === 'email' ? 'Reset Password' : 'Set New Password'}
          </Text>
          <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 14, marginTop: 6, textAlign: 'center', lineHeight: 20 }}>
            {step === 'email'
              ? 'Enter your Company ID and work email and we\'ll send a reset code'
              : 'Enter the code from your email and choose a new password'}
          </Text>
        </View>

        <View style={{ backgroundColor: 'rgba(255,255,255,0.07)', borderRadius: 20, padding: 20 }}>
          {step === 'email' ? (
            <>
              <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
                Company ID
              </Text>
              <TextInput
                value={tenantSlug}
                onChangeText={setTenantSlug}
                placeholder="e.g. acme-corp"
                placeholderTextColor="rgba(255,255,255,0.3)"
                autoCapitalize="none"
                autoCorrect={false}
                style={{
                  backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 12,
                  paddingHorizontal: 14, paddingVertical: 14, fontSize: 15,
                  color: '#fff', marginBottom: 16,
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
                keyboardType="email-address"
                style={{
                  backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 12,
                  paddingHorizontal: 14, paddingVertical: 14, fontSize: 15,
                  color: '#fff', marginBottom: 20,
                }}
              />
              <TouchableOpacity
                onPress={requestReset}
                disabled={loading}
                style={{
                  backgroundColor: loading ? 'rgba(47,107,255,0.5)' : COLORS.blue,
                  borderRadius: 12, padding: 16, alignItems: 'center',
                }}
              >
                {loading ? (
                  <ActivityIndicator color="#fff" />
                ) : (
                  <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Send Reset Code</Text>
                )}
              </TouchableOpacity>
            </>
          ) : (
            <>
              <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
                Reset Code
              </Text>
              <TextInput
                value={token}
                onChangeText={setToken}
                placeholder="Enter reset code from email"
                placeholderTextColor="rgba(255,255,255,0.3)"
                autoCapitalize="none"
                style={{
                  backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 12,
                  paddingHorizontal: 14, paddingVertical: 14, fontSize: 15,
                  color: '#fff', marginBottom: 16,
                }}
              />

              <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
                New Password
              </Text>
              <View style={{ position: 'relative', marginBottom: 16 }}>
                <TextInput
                  value={newPassword}
                  onChangeText={setNewPassword}
                  placeholder="Minimum 10 characters"
                  placeholderTextColor="rgba(255,255,255,0.3)"
                  secureTextEntry={!showPw}
                  style={{
                    backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 12,
                    paddingHorizontal: 14, paddingVertical: 14, paddingRight: 50,
                    fontSize: 15, color: '#fff',
                  }}
                />
                <TouchableOpacity
                  onPress={() => setShowPw(!showPw)}
                  style={{ position: 'absolute', right: 14, top: 14 }}
                >
                  <Text style={{ fontSize: 18 }}>{showPw ? '🙈' : '👁️'}</Text>
                </TouchableOpacity>
              </View>

              <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13, fontWeight: '600', marginBottom: 6 }}>
                Confirm Password
              </Text>
              <TextInput
                value={confirmPassword}
                onChangeText={setConfirmPassword}
                placeholder="Re-enter new password"
                placeholderTextColor="rgba(255,255,255,0.3)"
                secureTextEntry
                style={{
                  backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 12,
                  paddingHorizontal: 14, paddingVertical: 14, fontSize: 15,
                  color: '#fff', marginBottom: 20,
                }}
              />

              <TouchableOpacity
                onPress={submitReset}
                disabled={loading}
                style={{
                  backgroundColor: loading ? 'rgba(47,107,255,0.5)' : COLORS.blue,
                  borderRadius: 12, padding: 16, alignItems: 'center',
                }}
              >
                {loading ? (
                  <ActivityIndicator color="#fff" />
                ) : (
                  <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Reset Password</Text>
                )}
              </TouchableOpacity>

              <TouchableOpacity onPress={() => setStep('email')} style={{ alignItems: 'center', marginTop: 14 }}>
                <Text style={{ color: 'rgba(255,255,255,0.5)', fontSize: 13 }}>Resend code</Text>
              </TouchableOpacity>
            </>
          )}
        </View>
      </ScrollView>
    </View>
  );
}
