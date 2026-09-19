import React, { useState } from 'react';
import {
  View, Text, TouchableOpacity, TextInput,
  ActivityIndicator, Alert, ScrollView,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { useForm, Controller } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { authApi } from '@/api/adapters';
import { COLORS } from '@/config';

const schema = z.object({
  currentPassword: z.string().min(1, 'Required'),
  newPassword: z.string().min(10, 'At least 10 characters'),
  confirmPassword: z.string().min(1, 'Required'),
}).refine((d) => d.newPassword === d.confirmPassword, {
  message: 'Passwords do not match',
  path: ['confirmPassword'],
});
type FormData = z.infer<typeof schema>;

export default function ChangePasswordScreen() {
  const navigation = useNavigation();
  const [submitting, setSubmitting] = useState(false);
  const [showCurrent, setShowCurrent] = useState(false);
  const [showNew, setShowNew] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  const { control, handleSubmit, reset, formState: { errors } } = useForm<FormData>({
    resolver: zodResolver(schema),
  });

  const onSubmit = async (data: FormData) => {
    setSubmitting(true);
    try {
      await authApi.changePassword({
        currentPassword: data.currentPassword,
        newPassword: data.newPassword,
      });
      Alert.alert(
        'Password Changed',
        'Your password has been updated successfully.',
        [{ text: 'OK', onPress: () => navigation.goBack() }]
      );
      reset();
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to change password');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <TouchableOpacity onPress={() => navigation.goBack()} style={{ marginBottom: 10 }}>
          <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 14 }}>← Back</Text>
        </TouchableOpacity>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Change Password</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          Keep your account secure
        </Text>
      </View>

      <ScrollView contentContainerStyle={{ padding: 20 }} keyboardShouldPersistTaps="handled">
        <View style={{
          backgroundColor: '#fff', borderRadius: 16, padding: 20,
          shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
        }}>
          {/* Password strength hints */}
          <View style={{ backgroundColor: '#F0F9FF', borderRadius: 10, padding: 12, marginBottom: 20 }}>
            <Text style={{ fontSize: 12, color: '#0369A1', fontWeight: '600', marginBottom: 4 }}>Password requirements</Text>
            {['At least 10 characters', 'Mix of letters and numbers recommended'].map((h) => (
              <Text key={h} style={{ fontSize: 12, color: '#0369A1' }}>• {h}</Text>
            ))}
          </View>

          {[
            { name: 'currentPassword' as const, label: 'Current Password', show: showCurrent, setShow: setShowCurrent },
            { name: 'newPassword' as const, label: 'New Password', show: showNew, setShow: setShowNew },
            { name: 'confirmPassword' as const, label: 'Confirm New Password', show: showConfirm, setShow: setShowConfirm },
          ].map(({ name, label, show, setShow }) => (
            <View key={name} style={{ marginBottom: 16 }}>
              <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>{label} *</Text>
              <Controller
                control={control}
                name={name}
                render={({ field: { onChange, value } }) => (
                  <View style={{ position: 'relative' }}>
                    <TextInput
                      value={value}
                      onChangeText={onChange}
                      secureTextEntry={!show}
                      style={{
                        borderWidth: 1,
                        borderColor: errors[name] ? '#DC2626' : '#D1D5DB',
                        borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                        paddingRight: 50, fontSize: 15,
                      }}
                    />
                    <TouchableOpacity
                      onPress={() => setShow(!show)}
                      style={{ position: 'absolute', right: 14, top: 13 }}
                    >
                      <Text style={{ fontSize: 18 }}>{show ? '🙈' : '👁️'}</Text>
                    </TouchableOpacity>
                  </View>
                )}
              />
              {errors[name] && (
                <Text style={{ color: '#DC2626', fontSize: 12, marginTop: 2 }}>{errors[name]?.message}</Text>
              )}
            </View>
          ))}

          <TouchableOpacity
            onPress={handleSubmit(onSubmit)}
            disabled={submitting}
            style={{
              backgroundColor: submitting ? '#93C5FD' : COLORS.blue,
              borderRadius: 12, padding: 16, alignItems: 'center', marginTop: 8,
            }}
          >
            {submitting ? (
              <ActivityIndicator color="#fff" />
            ) : (
              <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Update Password</Text>
            )}
          </TouchableOpacity>
        </View>
      </ScrollView>
    </View>
  );
}
