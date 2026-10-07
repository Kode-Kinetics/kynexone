import React, { useState } from 'react';
import {
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation, useRoute } from '@react-navigation/native';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { attendanceApi } from '@/api/adapters';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const schema = z.object({
  date: z.string().min(1, 'Date required'),
  requestedClockIn: z.string().regex(/^\d{2}:\d{2}$/, 'Use HH:MM').optional().or(z.literal('')),
  requestedClockOut: z.string().regex(/^\d{2}:\d{2}$/, 'Use HH:MM').optional().or(z.literal('')),
  reason: z.string().min(5, 'Reason must be at least 5 characters'),
});
type FormData = z.infer<typeof schema>;

export default function AttendanceCorrectionScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { theme } = useTheme();
  const { width, fontScale } = useWindowDimensions();
  const stackTimes = width < 390 || fontScale > 1.15;
  const [submitting, setSubmitting] = useState(false);

  const { control, handleSubmit, formState: { errors } } = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: {
      date: route.params?.date ?? '',
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
        'Request submitted',
        'Your attendance correction was sent for review.',
        [{ text: 'Done', onPress: () => navigation.goBack() }],
      );
    } catch (error: any) {
      Alert.alert('Could not submit request', error?.message || 'Please try again.');
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
      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Attendance"
          title="Correct a punch"
          subtitle="Request a missing or incorrect clock-in or clock-out."
          onBack={() => navigation.goBack()}
        />

        <View style={styles.content}>
          <GlassSurface
            elevated={false}
            radius={theme.radius.xl}
            contentStyle={styles.infoCard}
            tintColor={theme.isDark ? 'rgba(245,158,11,0.12)' : 'rgba(255,247,237,0.72)'}
          >
            <View style={[styles.infoIcon, { backgroundColor: theme.colors.warning + '18' }]}>
              <Ionicons name="information-circle-outline" size={21} color={theme.colors.warning} />
            </View>
            <View style={styles.infoCopy}>
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                Reviewed before payroll
              </Text>
              <Text style={[theme.typography.caption, styles.infoText, { color: theme.colors.textSecondary }]}>
                Add only the punch that needs correction. Your manager and HR can review it before the attendance period is locked.
              </Text>
            </View>
          </GlassSurface>

          <GlassSurface radius={theme.radius.xl} contentStyle={styles.formCard}>
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
                  autoCapitalize="none"
                  keyboardType="numbers-and-punctuation"
                  error={errors.date?.message}
                />
              )}
            />

            <Text style={[theme.typography.caption, styles.helper, { color: theme.colors.textMuted }]}>
              Leave a time blank when that punch is already correct.
            </Text>

            <View style={[styles.timeRow, stackTimes && styles.timeRowStacked]}>
              <Controller
                control={control}
                name="requestedClockIn"
                render={({ field: { onChange, value, onBlur } }) => (
                  <GlassTextField
                    containerStyle={styles.timeField}
                    label="Clock-in"
                    icon="log-in-outline"
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    placeholder="09:00"
                    keyboardType="numbers-and-punctuation"
                    error={errors.requestedClockIn?.message}
                  />
                )}
              />
              <Controller
                control={control}
                name="requestedClockOut"
                render={({ field: { onChange, value, onBlur } }) => (
                  <GlassTextField
                    containerStyle={styles.timeField}
                    label="Clock-out"
                    icon="log-out-outline"
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    placeholder="18:00"
                    keyboardType="numbers-and-punctuation"
                    error={errors.requestedClockOut?.message}
                  />
                )}
              />
            </View>

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
                  placeholder="What happened and what should be corrected?"
                  multiline
                  textAlignVertical="top"
                  error={errors.reason?.message}
                  style={styles.reasonInput}
                />
              )}
            />

            <LiquidButton
              label="Submit correction"
              icon="paper-plane-outline"
              onPress={handleSubmit(onSubmit)}
              loading={submitting}
              disabled={submitting}
            />
          </GlassSurface>

          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            Corrections remain auditable and may require manager and HR approval.
          </Text>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 12 },
  infoCard: { flexDirection: 'row', alignItems: 'flex-start', gap: 11, padding: 14 },
  infoIcon: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  infoCopy: { flex: 1, minWidth: 0 },
  infoText: { marginTop: 4, lineHeight: 18 },
  formCard: { padding: 18 },
  helper: { marginTop: -4, marginBottom: 12 },
  timeRow: { flexDirection: 'row', gap: 10 },
  timeRowStacked: { flexDirection: 'column', gap: 0 },
  timeField: { flex: 1 },
  reasonInput: { minHeight: 100, paddingTop: 14 },
  footer: { textAlign: 'center', paddingHorizontal: 18, marginTop: 2 },
});
