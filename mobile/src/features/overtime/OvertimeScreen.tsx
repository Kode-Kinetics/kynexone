import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  FlatList,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { overtimeApi } from '@/api/adapters';
import type { OvertimeRequest } from '@/types';
import { formatDate, formatDuration, toISODate } from '@/utils/date';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const schema = z.object({
  date: z.string().min(1, 'Date is required'),
  startTime: z.string().regex(/^\d{2}:\d{2}$/, 'Use HH:MM'),
  endTime: z.string().regex(/^\d{2}:\d{2}$/, 'Use HH:MM'),
  reason: z.string().min(5, 'Reason must be at least 5 characters'),
});
type FormData = z.infer<typeof schema>;

function calcDurationMins(start: string, end: string): number {
  const [startHour, startMinute] = start.split(':').map(Number);
  const [endHour, endMinute] = end.split(':').map(Number);
  if ([startHour, startMinute, endHour, endMinute].some(Number.isNaN)) return 0;
  const difference = endHour * 60 + endMinute - (startHour * 60 + startMinute);
  return difference > 0 ? difference : difference + 24 * 60;
}

export default function OvertimeScreen() {
  const { theme } = useTheme();
  const { width, fontScale } = useWindowDimensions();
  const stackTimes = width < 390 || fontScale > 1.15;
  const [tab, setTab] = useState<'apply' | 'history'>('apply');
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [preview, setPreview] = useState<{ hours: number; amount?: number } | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);

  const {
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<FormData>({
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
  const durationMins = calcDurationMins(startTime, endTime);

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

  const fetchRequests = useCallback(async (refresh = false) => {
    if (refresh) setRefreshing(true);
    else setLoading(true);
    try {
      const data = await overtimeApi.getMy({ page: 1, limit: 50 });
      setRequests(data.items || []);
    } catch (error: any) {
      Alert.alert('Could not load overtime', error?.message || 'Please try again.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    if (tab === 'history') void fetchRequests();
  }, [fetchRequests, tab]);

  const onSubmit = async (data: FormData) => {
    setSubmitting(true);
    try {
      await overtimeApi.create({
        date: data.date,
        startTime: data.startTime,
        endTime: data.endTime,
        reason: data.reason,
      });
      Alert.alert('Request submitted', 'Your overtime request was sent for approval.');
      reset({
        date: toISODate(new Date()),
        startTime: '18:00',
        endTime: '21:00',
        reason: '',
      });
      setPreview(null);
    } catch (error: any) {
      Alert.alert('Could not submit overtime', error?.message || 'Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop subtle />
      <View style={styles.header}>
        <ScreenHero
          eyebrow="Time & attendance"
          title="Overtime"
          subtitle="Submit extra hours and follow their approval status."
        />
        <GlassSurface
          elevated={false}
          radius={18}
          style={styles.tabsSurface}
          contentStyle={styles.tabs}
        >
          <TabButton
            label="Apply"
            icon="add-circle-outline"
            active={tab === 'apply'}
            onPress={() => setTab('apply')}
          />
          <TabButton
            label="History"
            icon="time-outline"
            active={tab === 'history'}
            onPress={() => setTab('history')}
          />
        </GlassSurface>
      </View>

      {tab === 'apply' ? (
        <ScrollView
          contentContainerStyle={styles.applyScroll}
          keyboardShouldPersistTaps="handled"
          keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
          automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
          showsVerticalScrollIndicator={false}
        >
          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            <Controller
              control={control}
              name="date"
              render={({ field: { onChange, value, onBlur } }) => (
                <GlassTextField
                  label="Date"
                  icon="calendar-outline"
                  value={value}
                  onChangeText={onChange}
                  onBlur={onBlur}
                  placeholder="YYYY-MM-DD"
                  keyboardType="numbers-and-punctuation"
                  error={errors.date?.message}
                />
              )}
            />

            <View style={[styles.timeRow, stackTimes && styles.timeRowStacked]}>
              <Controller
                control={control}
                name="startTime"
                render={({ field: { onChange, value, onBlur } }) => (
                  <GlassTextField
                    containerStyle={styles.timeField}
                    label="Start time"
                    icon="play-outline"
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    placeholder="18:00"
                    keyboardType="numbers-and-punctuation"
                    error={errors.startTime?.message}
                  />
                )}
              />
              <Controller
                control={control}
                name="endTime"
                render={({ field: { onChange, value, onBlur } }) => (
                  <GlassTextField
                    containerStyle={styles.timeField}
                    label="End time"
                    icon="stop-outline"
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    placeholder="21:00"
                    keyboardType="numbers-and-punctuation"
                    error={errors.endTime?.message}
                  />
                )}
              />
            </View>

            {durationMins > 0 ? (
              <GlassSurface
                elevated={false}
                radius={18}
                contentStyle={styles.preview}
                tintColor={theme.isDark ? 'rgba(47,107,255,0.12)' : 'rgba(47,107,255,0.08)'}
              >
                <View style={styles.previewMetric}>
                  <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
                    DURATION
                  </Text>
                  <Text style={[theme.typography.h2, { color: theme.colors.text }]}>
                    {formatDuration(durationMins)}
                  </Text>
                </View>
                <View style={[styles.previewDivider, { backgroundColor: theme.colors.divider }]} />
                <View style={[styles.previewMetric, styles.previewRight]}>
                  <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
                    ESTIMATED PAYOUT
                  </Text>
                  {previewLoading ? (
                    <ActivityIndicator color={theme.colors.primary} />
                  ) : preview?.amount !== undefined ? (
                    <Text style={[theme.typography.h2, { color: theme.colors.primary }]}>
                      {preview.amount.toFixed(2)}
                    </Text>
                  ) : (
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
                      Calculated by payroll policy
                    </Text>
                  )}
                </View>
              </GlassSurface>
            ) : null}

            <Controller
              control={control}
              name="reason"
              render={({ field: { onChange, value, onBlur } }) => (
                <GlassTextField
                  label="Reason"
                  icon="chatbox-ellipses-outline"
                  value={value}
                  onChangeText={onChange}
                  onBlur={onBlur}
                  placeholder="Why are these overtime hours required?"
                  multiline
                  textAlignVertical="top"
                  error={errors.reason?.message}
                  style={styles.reasonInput}
                />
              )}
            />

            <LiquidButton
              label="Submit overtime"
              icon="paper-plane-outline"
              onPress={handleSubmit(onSubmit)}
              loading={submitting}
              disabled={submitting}
            />
          </GlassSurface>

          <GlassSurface elevated={false} radius={18} contentStyle={styles.policyNote}>
            <Ionicons name="information-circle-outline" size={19} color={theme.colors.primary} />
            <Text style={[theme.typography.caption, styles.policyText, { color: theme.colors.textSecondary }]}>
              Estimated payout is informational until the request is approved and processed by payroll.
            </Text>
          </GlassSurface>
        </ScrollView>
      ) : (
        <FlatList
          data={requests}
          keyExtractor={(item) => item.id}
          contentContainerStyle={[
            styles.historyList,
            requests.length === 0 && styles.historyEmptyList,
          ]}
          refreshing={refreshing}
          onRefresh={() => void fetchRequests(true)}
          showsVerticalScrollIndicator={false}
          ListEmptyComponent={
            loading ? (
              <View style={styles.loadingHistory}>
                <ActivityIndicator color={theme.colors.primary} />
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                  Loading overtime history…
                </Text>
              </View>
            ) : (
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
                <View style={[styles.emptyIcon, { backgroundColor: theme.colors.primary + '16' }]}>
                  <Ionicons name="time-outline" size={26} color={theme.colors.primary} />
                </View>
                <Text style={[theme.typography.h3, { color: theme.colors.text }]}>
                  No overtime requests
                </Text>
                <Text style={[theme.typography.caption, styles.emptyCopy, { color: theme.colors.textSecondary }]}>
                  Submitted overtime requests and approval status will appear here.
                </Text>
              </GlassSurface>
            )
          }
          renderItem={({ item }) => <OvertimeCard request={item} />}
        />
      )}
    </KeyboardAvoidingView>
  );
}

function TabButton({
  label,
  icon,
  active,
  onPress,
}: {
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  active: boolean;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  return (
    <MotionPressable
      accessibilityRole="tab"
      accessibilityState={{ selected: active }}
      accessibilityLabel={label + ' overtime tab'}
      onPress={onPress}
      haptic="selection"
      style={styles.tabShell}
      contentStyle={[
        styles.tabButton,
        active && { backgroundColor: theme.colors.surfaceSoft },
      ]}
    >
      <Ionicons
        name={active ? icon.replace('-outline', '') as any : icon}
        size={18}
        color={active ? theme.colors.primary : theme.colors.textMuted}
      />
      <Text
        style={[
          theme.typography.bodyStrong,
          { color: active ? theme.colors.text : theme.colors.textMuted },
        ]}
      >
        {label}
      </Text>
    </MotionPressable>
  );
}

function OvertimeCard({ request }: { request: OvertimeRequest }) {
  const { theme } = useTheme();
  const duration = request.durationMinutes ?? Math.round(request.totalHours * 60);

  return (
    <GlassSurface
      elevated={false}
      radius={theme.radius.xl}
      style={styles.requestSurface}
      contentStyle={styles.requestCard}
    >
      <View style={styles.requestHeader}>
        <View style={styles.requestTitle}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
            {formatDate(request.date, 'display')}
          </Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
            {request.startTime} – {request.endTime}
          </Text>
        </View>
        <StatusBadge status={request.status} />
      </View>

      <View style={[styles.requestMetrics, { backgroundColor: theme.colors.surfaceSoft }]}>
        <View style={styles.requestMetric}>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>DURATION</Text>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
            {formatDuration(duration)}
          </Text>
        </View>
        {request.calculatedAmount !== undefined ? (
          <View style={styles.requestMetric}>
            <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>PAYOUT</Text>
            <Text style={[theme.typography.bodyStrong, { color: theme.colors.primary }]}>
              {request.calculatedAmount.toFixed(2)}
            </Text>
          </View>
        ) : null}
      </View>

      {request.reason ? (
        <Text
          numberOfLines={3}
          style={[theme.typography.caption, styles.reason, { color: theme.colors.textSecondary }]}
        >
          {request.reason}
        </Text>
      ) : null}
    </GlassSurface>
  );
}

function StatusBadge({ status }: { status: string }) {
  const { theme } = useTheme();
  const normalized = status.toLowerCase();
  const color = normalized.includes('approve')
    ? theme.colors.success
    : normalized.includes('reject') || normalized.includes('cancel')
      ? theme.colors.danger
      : theme.colors.warning;

  return (
    <View style={[styles.statusBadge, { backgroundColor: color + '16' }]}>
      <View style={[styles.statusDot, { backgroundColor: color }]} />
      <Text style={[theme.typography.micro, { color }]}>{status}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  header: { paddingBottom: 8 },
  tabsSurface: { marginHorizontal: 16, marginTop: 8 },
  tabs: { flexDirection: 'row', padding: 5 },
  tabShell: { flex: 1 },
  tabButton: {
    minHeight: 48,
    borderRadius: 14,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 7,
  },
  applyScroll: { paddingHorizontal: 16, paddingTop: 8, paddingBottom: 38, gap: 12 },
  form: { padding: 18 },
  timeRow: { flexDirection: 'row', gap: 10 },
  timeRowStacked: { flexDirection: 'column', gap: 0 },
  timeField: { flex: 1 },
  preview: {
    minHeight: 84,
    flexDirection: 'row',
    alignItems: 'stretch',
    marginBottom: 16,
    paddingVertical: 12,
  },
  previewMetric: { flex: 1, justifyContent: 'center', gap: 4, paddingHorizontal: 14 },
  previewRight: { alignItems: 'flex-end' },
  previewDivider: { width: StyleSheet.hairlineWidth },
  reasonInput: { minHeight: 100, paddingTop: 14 },
  policyNote: { flexDirection: 'row', alignItems: 'flex-start', gap: 9, padding: 13 },
  policyText: { flex: 1, lineHeight: 18 },
  historyList: { paddingHorizontal: 16, paddingTop: 8, paddingBottom: 38 },
  historyEmptyList: { flexGrow: 1, justifyContent: 'center' },
  loadingHistory: { alignItems: 'center', justifyContent: 'center', gap: 12, padding: 28 },
  emptyCard: { alignItems: 'center', padding: 24, gap: 8 },
  emptyIcon: {
    width: 52,
    height: 52,
    borderRadius: 18,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 2,
  },
  emptyCopy: { maxWidth: 280, textAlign: 'center', lineHeight: 18 },
  requestSurface: { marginBottom: 12 },
  requestCard: { padding: 15 },
  requestHeader: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  requestTitle: { flex: 1, minWidth: 0, gap: 2 },
  requestMetrics: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 22,
    borderRadius: 14,
    padding: 11,
    marginTop: 12,
  },
  requestMetric: { gap: 2 },
  reason: { marginTop: 10, lineHeight: 18 },
  statusBadge: {
    minHeight: 30,
    borderRadius: 999,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 5,
    paddingHorizontal: 9,
  },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
});
