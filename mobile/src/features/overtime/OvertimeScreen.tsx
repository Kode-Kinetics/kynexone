import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, TextInput,
  ActivityIndicator, Alert, RefreshControl,
} from 'react-native';
import { useForm, Controller, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { overtimeApi } from '@/api/adapters';
import { OvertimeRequest } from '@/types';
import { formatDate, formatDuration, toISODate } from '@/utils/date';
import { COLORS } from '@/config';

// ─── Schema ────────────────────────────────────────────────────────────────
const schema = z.object({
  date: z.string().min(1, 'Date is required'),
  startTime: z.string().regex(/^\d{2}:\d{2}$/, 'Format HH:MM'),
  endTime: z.string().regex(/^\d{2}:\d{2}$/, 'Format HH:MM'),
  reason: z.string().min(5, 'Reason must be at least 5 characters'),
});
type FormData = z.infer<typeof schema>;

// ─── Helpers ────────────────────────────────────────────────────────────────
function calcDurationMins(start: string, end: string): number {
  const [sh, sm] = start.split(':').map(Number);
  const [eh, em] = end.split(':').map(Number);
  if ([sh, sm, eh, em].some((n) => Number.isNaN(n))) return 0;
  const diff = (eh * 60 + em) - (sh * 60 + sm);
  // An end time at or before the start is an overnight block (e.g. 22:00 → 02:00).
  return diff > 0 ? diff : diff + 24 * 60;
}

function StatusBadge({ status }: { status: string }) {
  const map: Record<string, { bg: string; text: string; label: string }> = {
    Pending:  { bg: '#FFF7ED', text: '#C2410C', label: 'Pending' },
    Approved: { bg: '#F0FDF4', text: '#15803D', label: 'Approved' },
    Rejected: { bg: '#FEF2F2', text: '#DC2626', label: 'Rejected' },
    Cancelled:{ bg: '#F3F4F6', text: '#6B7280', label: 'Cancelled' },
  };
  const s = map[status] ?? map['Pending'];
  return (
    <View style={{ backgroundColor: s.bg, borderRadius: 6, paddingHorizontal: 8, paddingVertical: 2 }}>
      <Text style={{ color: s.text, fontSize: 11, fontWeight: '600' }}>{s.label}</Text>
    </View>
  );
}

// ─── Main ────────────────────────────────────────────────────────────────────
export default function OvertimeScreen() {
  const [tab, setTab] = useState<'apply' | 'history'>('apply');
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [preview, setPreview] = useState<{ hours: number; amount?: number } | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);

  const { control, handleSubmit, reset, formState: { errors } } = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: {
      date: toISODate(new Date()),
      startTime: '18:00',
      endTime: '21:00',
      reason: '',
    },
  });

  const startTime = useWatch({ control, name: 'startTime' });
  const endTime = useWatch({ control, name: 'endTime' });
  const date = useWatch({ control, name: 'date' });

  // Duration preview
  const durationMins = calcDurationMins(startTime, endTime);

  // Fetch OT calculation preview
  useEffect(() => {
    if (!date || !startTime || !endTime || durationMins <= 0) {
      setPreview(null);
      return;
    }
    const timer = setTimeout(async () => {
      setPreviewLoading(true);
      try {
        const data = await overtimeApi.calculate({ date, startTime, endTime });
        setPreview({ hours: data.hours, amount: data.estimatedAmount });
      } catch {
        setPreview({ hours: durationMins / 60 });
      } finally {
        setPreviewLoading(false);
      }
    }, 800);
    return () => clearTimeout(timer);
  }, [date, durationMins, endTime, startTime]);

  const fetchRequests = useCallback(async () => {
    setLoading(true);
    try {
      const data = await overtimeApi.getMy({ page: 1, limit: 50 });
      setRequests(data.items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load overtime requests');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { if (tab === 'history') fetchRequests(); }, [tab, fetchRequests]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchRequests();
    setRefreshing(false);
  };

  const onSubmit = async (data: FormData) => {
    setSubmitting(true);
    try {
      await overtimeApi.create({
        date: data.date,
        startTime: data.startTime,
        endTime: data.endTime,
        reason: data.reason,
      });
      Alert.alert('Submitted', 'Your overtime request was sent to your manager for approval.');
      reset();
      setPreview(null);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to submit overtime request');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Overtime</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          Submit and track overtime requests
        </Text>
      </View>

      {/* Tabs */}
      <View style={{ flexDirection: 'row', backgroundColor: '#fff', borderBottomWidth: 1, borderBottomColor: '#E5E7EB' }}>
        {(['apply', 'history'] as const).map((t2) => (
          <TouchableOpacity
            key={t2}
            onPress={() => setTab(t2)}
            style={{
              flex: 1, paddingVertical: 14, alignItems: 'center',
              borderBottomWidth: 2,
              borderBottomColor: tab === t2 ? COLORS.blue : 'transparent',
            }}
          >
            <Text style={{ color: tab === t2 ? COLORS.blue : '#6B7280', fontWeight: tab === t2 ? '600' : '400' }}>
              {t2 === 'apply' ? 'Apply' : 'History'}
            </Text>
          </TouchableOpacity>
        ))}
      </View>

      {tab === 'apply' ? (
        <ScrollView contentContainerStyle={{ padding: 20, paddingBottom: 40 }} keyboardShouldPersistTaps="handled">
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
                  backgroundColor: '#fff', fontSize: 15, marginBottom: 4,
                }}
              />
            )}
          />
          {errors.date && <Text style={{ color: '#DC2626', fontSize: 12, marginBottom: 8 }}>{errors.date.message}</Text>}

          {/* Time row */}
          <View style={{ flexDirection: 'row', gap: 12, marginTop: 8 }}>
            <View style={{ flex: 1 }}>
              <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Start Time *</Text>
              <Controller
                control={control}
                name="startTime"
                render={({ field: { onChange, value } }) => (
                  <TextInput
                    value={value}
                    onChangeText={onChange}
                    placeholder="18:00"
                    style={{
                      borderWidth: 1, borderColor: errors.startTime ? '#DC2626' : '#D1D5DB',
                      borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                      backgroundColor: '#fff', fontSize: 15,
                    }}
                  />
                )}
              />
              {errors.startTime && <Text style={{ color: '#DC2626', fontSize: 12 }}>{errors.startTime.message}</Text>}
            </View>
            <View style={{ flex: 1 }}>
              <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>End Time *</Text>
              <Controller
                control={control}
                name="endTime"
                render={({ field: { onChange, value } }) => (
                  <TextInput
                    value={value}
                    onChangeText={onChange}
                    placeholder="21:00"
                    style={{
                      borderWidth: 1, borderColor: errors.endTime ? '#DC2626' : '#D1D5DB',
                      borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                      backgroundColor: '#fff', fontSize: 15,
                    }}
                  />
                )}
              />
              {errors.endTime && <Text style={{ color: '#DC2626', fontSize: 12 }}>{errors.endTime.message}</Text>}
            </View>
          </View>

          {/* Duration / Preview card */}
          {durationMins > 0 && (
            <View style={{
              backgroundColor: '#EFF6FF', borderRadius: 12, padding: 14, marginTop: 12,
              flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
            }}>
              <View>
                <Text style={{ fontSize: 13, color: '#1E40AF', fontWeight: '600' }}>Duration</Text>
                <Text style={{ fontSize: 20, color: COLORS.blue, fontWeight: '700', marginTop: 2 }}>
                  {formatDuration(durationMins)}
                </Text>
              </View>
              {previewLoading ? (
                <ActivityIndicator color={COLORS.blue} />
              ) : preview?.amount ? (
                <View style={{ alignItems: 'flex-end' }}>
                  <Text style={{ fontSize: 12, color: '#1E40AF' }}>Est. Payout</Text>
                  <Text style={{ fontSize: 18, color: COLORS.blue, fontWeight: '700' }}>
                    {preview.amount.toFixed(2)}
                  </Text>
                </View>
              ) : null}
            </View>
          )}

          {/* Reason */}
          <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginTop: 16, marginBottom: 6 }}>Reason *</Text>
          <Controller
            control={control}
            name="reason"
            render={({ field: { onChange, value } }) => (
              <TextInput
                value={value}
                onChangeText={onChange}
                placeholder="Enter reason for overtime..."
                multiline
                numberOfLines={4}
                textAlignVertical="top"
                style={{
                  borderWidth: 1, borderColor: errors.reason ? '#DC2626' : '#D1D5DB',
                  borderRadius: 10, paddingHorizontal: 14, paddingVertical: 12,
                  backgroundColor: '#fff', fontSize: 15, minHeight: 100,
                }}
              />
            )}
          />
          {errors.reason && <Text style={{ color: '#DC2626', fontSize: 12, marginTop: 2 }}>{errors.reason.message}</Text>}

          {/* Submit */}
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
              <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Submit Overtime Request</Text>
            )}
          </TouchableOpacity>
        </ScrollView>
      ) : (
        /* History */
        <ScrollView
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
          contentContainerStyle={{ padding: 16 }}
        >
          {loading ? (
            <ActivityIndicator color={COLORS.blue} style={{ marginTop: 40 }} />
          ) : requests.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 60 }}>
              <Text style={{ fontSize: 40 }}>⏰</Text>
              <Text style={{ color: '#374151', fontSize: 16, fontWeight: '600', marginTop: 12 }}>No overtime requests</Text>
              <Text style={{ color: '#9CA3AF', fontSize: 13, marginTop: 4 }}>Your overtime history will appear here</Text>
            </View>
          ) : (
            requests.map((req) => (
              <View key={req.id} style={{
                backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 12,
                shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
              }}>
                <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                  <View>
                    <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827' }}>
                      {formatDate(req.date, 'display')}
                    </Text>
                    <Text style={{ fontSize: 13, color: '#6B7280', marginTop: 2 }}>
                      {req.startTime} – {req.endTime}
                    </Text>
                  </View>
                  <StatusBadge status={req.status} />
                </View>
                <View style={{
                  flexDirection: 'row', alignItems: 'center', backgroundColor: '#F9FAFB',
                  borderRadius: 8, padding: 10, marginTop: 12, gap: 16,
                }}>
                  <View style={{ alignItems: 'center' }}>
                    <Text style={{ fontSize: 11, color: '#9CA3AF' }}>Duration</Text>
                    <Text style={{ fontSize: 14, fontWeight: '600', color: '#374151' }}>
                      {formatDuration(req.durationMinutes ?? Math.round(req.totalHours * 60))}
                    </Text>
                  </View>
                  {req.calculatedAmount !== undefined && (
                    <View style={{ alignItems: 'center' }}>
                      <Text style={{ fontSize: 11, color: '#9CA3AF' }}>Payout</Text>
                      <Text style={{ fontSize: 14, fontWeight: '600', color: COLORS.blue }}>
                        {req.calculatedAmount.toFixed(2)}
                      </Text>
                    </View>
                  )}
                </View>
                {req.reason ? (
                  <Text style={{ fontSize: 13, color: '#6B7280', marginTop: 8 }} numberOfLines={2}>{req.reason}</Text>
                ) : null}
              </View>
            ))
          )}
        </ScrollView>
      )}
    </View>
  );
}
