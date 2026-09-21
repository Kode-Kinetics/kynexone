import React, { useState } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, TextInput,
  ActivityIndicator, Alert,
} from 'react-native';
import { useRoute, useNavigation } from '@react-navigation/native';
import { useForm, Controller } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { attendanceApi } from '@/api/adapters';
import { COLORS } from '@/config';

const schema = z.object({
  date: z.string().min(1, 'Date required'),
  requestedClockIn: z.string().regex(/^\d{2}:\d{2}$/, 'Format HH:MM').optional().or(z.literal('')),
  requestedClockOut: z.string().regex(/^\d{2}:\d{2}$/, 'Format HH:MM').optional().or(z.literal('')),
  reason: z.string().min(5, 'Reason must be at least 5 characters'),
});
type FormData = z.infer<typeof schema>;

export default function AttendanceCorrectionScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const [submitting, setSubmitting] = useState(false);

  // Pre-fill date from navigation params if coming from attendance history
  const prefillDate: string = route.params?.date ?? '';

  const { control, handleSubmit, formState: { errors } } = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: {
      date: prefillDate,
      requestedClockIn: '',
      requestedClockOut: '',
      reason: '',
    },
  });

  const onSubmit = async (data: FormData) => {
    setSubmitting(true);
    try {
      await attendanceApi.regularize({
        date: data.date,
        requestedClockIn: data.requestedClockIn || undefined,
        requestedClockOut: data.requestedClockOut || undefined,
        reason: data.reason,
      });
      Alert.alert(
        'Submitted',
        'Attendance correction request submitted. Your supervisor will review it.',
        [{ text: 'OK', onPress: () => navigation.goBack() }]
      );
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to submit correction request');
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
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Attendance Correction</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          Request a missing punch or correction
        </Text>
      </View>

      <ScrollView contentContainerStyle={{ padding: 20 }} keyboardShouldPersistTaps="handled">
        <View style={{
          backgroundColor: '#FFF7ED', borderRadius: 12, padding: 14, marginBottom: 20,
          borderLeftWidth: 4, borderLeftColor: '#F59E0B',
        }}>
          <Text style={{ fontWeight: '700', color: '#92400E', fontSize: 14 }}>📋 How it works</Text>
          <Text style={{ color: '#92400E', fontSize: 13, marginTop: 4, lineHeight: 18 }}>
            Submit this form if you have a missing punch or incorrect attendance record. Your supervisor will review and approve the correction.
          </Text>
        </View>

        {/* Date */}
        <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Date *</Text>
        <Controller
          control={control}
          name="date"
          render={({ field: { onChange, value } }) => (
            <TextInput
              value={value}
              onChangeText={onChange}
              placeholder="YYYY-MM-DD"
              style={{
                borderWidth: 1, borderColor: errors.date ? '#DC2626' : '#D1D5DB',
                borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                fontSize: 15, backgroundColor: '#fff', marginBottom: 4,
              }}
            />
          )}
        />
        {errors.date && <Text style={{ color: '#DC2626', fontSize: 12, marginBottom: 8 }}>{errors.date.message}</Text>}

        {/* Clock times */}
        <Text style={{ fontSize: 13, color: '#6B7280', marginBottom: 12, marginTop: 4 }}>
          Leave blank if no correction needed for that punch
        </Text>
        <View style={{ flexDirection: 'row', gap: 12 }}>
          <View style={{ flex: 1 }}>
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Requested Clock-In</Text>
            <Controller
              control={control}
              name="requestedClockIn"
              render={({ field: { onChange, value } }) => (
                <TextInput
                  value={value}
                  onChangeText={onChange}
                  placeholder="09:00"
                  style={{
                    borderWidth: 1, borderColor: errors.requestedClockIn ? '#DC2626' : '#D1D5DB',
                    borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                    fontSize: 15, backgroundColor: '#fff',
                  }}
                />
              )}
            />
            {errors.requestedClockIn && (
              <Text style={{ color: '#DC2626', fontSize: 12, marginTop: 2 }}>{errors.requestedClockIn.message}</Text>
            )}
          </View>
          <View style={{ flex: 1 }}>
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Requested Clock-Out</Text>
            <Controller
              control={control}
              name="requestedClockOut"
              render={({ field: { onChange, value } }) => (
                <TextInput
                  value={value}
                  onChangeText={onChange}
                  placeholder="18:00"
                  style={{
                    borderWidth: 1, borderColor: errors.requestedClockOut ? '#DC2626' : '#D1D5DB',
                    borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                    fontSize: 15, backgroundColor: '#fff',
                  }}
                />
              )}
            />
            {errors.requestedClockOut && (
              <Text style={{ color: '#DC2626', fontSize: 12, marginTop: 2 }}>{errors.requestedClockOut.message}</Text>
            )}
          </View>
        </View>

        {/* Reason */}
        <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginTop: 16, marginBottom: 6 }}>Reason *</Text>
        <Controller
          control={control}
          name="reason"
          render={({ field: { onChange, value } }) => (
            <TextInput
              value={value}
              onChangeText={onChange}
              placeholder="Explain why this correction is needed (e.g. forgot to punch in, system outage, working from client site)"
              multiline
              numberOfLines={5}
              textAlignVertical="top"
              style={{
                borderWidth: 1, borderColor: errors.reason ? '#DC2626' : '#D1D5DB',
                borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                fontSize: 15, backgroundColor: '#fff', minHeight: 120,
              }}
            />
          )}
        />
        {errors.reason && <Text style={{ color: '#DC2626', fontSize: 12, marginTop: 2 }}>{errors.reason.message}</Text>}

        <TouchableOpacity
          onPress={handleSubmit(onSubmit)}
          disabled={submitting}
          style={{
            backgroundColor: submitting ? '#93C5FD' : COLORS.blue,
            borderRadius: 12, padding: 16, alignItems: 'center', marginTop: 24,
          }}
        >
          {submitting ? (
            <ActivityIndicator color="#fff" />
          ) : (
            <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Submit Correction Request</Text>
          )}
        </TouchableOpacity>
      </ScrollView>
    </View>
  );
}
