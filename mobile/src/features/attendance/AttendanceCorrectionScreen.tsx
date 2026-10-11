// Fix a punch: pick the day and the time(s) with the system pickers (no typed "HH:MM", which
// rejected Arabic digits), say why, send for review. Times are company time (Asia/Riyadh).

import React, { useState } from 'react';
import {
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation, useRoute } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import DateTimePicker, { DateTimePickerAndroid, type DateTimePickerEvent } from '@react-native-community/datetimepicker';
import { attendanceApi } from '@/api/adapters';
import { extractErrorMessage } from '@/api/client';
import {
  BottomSheet,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { pickerDate, validateCorrection, wallDateOf, wallTimeOf, type CorrectionError } from './companyTime';

type Field = 'date' | 'clockIn' | 'clockOut';

const ERROR_KEY: Record<CorrectionError, 'home.correction.dateRequired' | 'home.correction.timeRequired' | 'home.correction.orderInvalid' | 'home.correction.reasonShort'> = {
  dateRequired: 'home.correction.dateRequired',
  timeRequired: 'home.correction.timeRequired',
  orderInvalid: 'home.correction.orderInvalid',
  reasonShort: 'home.correction.reasonShort',
};

export default function AttendanceCorrectionScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const today = f.businessDate() ?? '';
  const initialDate = typeof route.params?.date === 'string' && route.params.date <= today ? route.params.date : null;

  const [date, setDate] = useState<string | null>(initialDate);
  const [clockIn, setClockIn] = useState<string | null>(null);
  const [clockOut, setClockOut] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [showErrors, setShowErrors] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [iosPicker, setIosPicker] = useState<{ field: Field; value: Date } | null>(null);

  const errors = validateCorrection({ date, clockIn, clockOut, reason });
  const visibleErrors = showErrors ? errors : {};

  const apply = (field: Field, value: Date) => {
    if (field === 'date') setDate(wallDateOf(value));
    else if (field === 'clockIn') setClockIn(wallTimeOf(value));
    else setClockOut(wallTimeOf(value));
  };

  const initialValue = (field: Field): Date => {
    const day = date ?? today;
    if (field === 'date') return pickerDate(day);
    const current = field === 'clockIn' ? clockIn : clockOut;
    return pickerDate(day, current ?? (field === 'clockIn' ? '08:00' : '17:00'));
  };

  const openPicker = (field: Field) => {
    const value = initialValue(field);
    const maximumDate = field === 'date' && today ? pickerDate(today) : undefined;
    if (Platform.OS === 'android') {
      DateTimePickerAndroid.open({
        value,
        mode: field === 'date' ? 'date' : 'time',
        is24Hour: false,
        maximumDate,
        onChange: (event: DateTimePickerEvent, picked?: Date) => {
          if (event.type === 'set' && picked) apply(field, picked);
        },
      });
      return;
    }
    setIosPicker({ field, value });
  };

  const onSubmit = async () => {
    if (Object.keys(errors).length > 0) {
      setShowErrors(true);
      haptics.error();
      return;
    }
    setSubmitting(true);
    try {
      await attendanceApi.regularize({
        date: date!,
        requestedClockIn: clockIn ?? undefined,
        requestedClockOut: clockOut ?? undefined,
        reason: reason.trim(),
      });
      haptics.success();
      Alert.alert(
        t('home.correction.submittedTitle'),
        t('home.correction.submittedBody', { date: f.date(date, 'long') }),
        [{ text: t('home.correction.done'), onPress: () => navigation.goBack() }],
      );
    } catch (error: unknown) {
      haptics.error();
      // The server's own plain message (e.g. a corrected time outside that work day), not axios's status text.
      Alert.alert(t('home.correction.failedTitle'), extractErrorMessage(error) || t('home.correction.failedBody'));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
    >
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('home.correction.eyebrow')}
          title={t('home.correction.title')}
          subtitle={t('home.correction.subtitle')}
        />

        <View style={styles.content}>
          <Surface level={0} contentStyle={styles.infoCard}>
            <Ionicons accessible={false} name="information-circle-outline" size={20} color={theme.colors.status.info.fg} />
            <View style={styles.flex}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('home.correction.infoTitle')}</Text>
              <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('home.correction.infoBody')}</Text>
            </View>
          </Surface>

          <Surface contentStyle={styles.formCard}>
            <PickerField
              label={t('home.correction.date')}
              icon="calendar-outline"
              value={date ? f.date(date, 'full') : null}
              placeholder={t('home.correction.chooseDate')}
              error={visibleErrors.date ? t(ERROR_KEY[visibleErrors.date]) : undefined}
              onPress={() => openPicker('date')}
            />

            <View style={styles.timeRow}>
              <PickerField
                style={styles.flex}
                label={t('home.correction.clockIn')}
                icon="log-in-outline"
                value={clockIn ? f.time(clockIn) : null}
                placeholder={t('home.correction.notChanged')}
                onPress={() => openPicker('clockIn')}
                onClear={clockIn ? () => setClockIn(null) : undefined}
              />
              <PickerField
                style={styles.flex}
                label={t('home.correction.clockOut')}
                icon="log-out-outline"
                value={clockOut ? f.time(clockOut) : null}
                placeholder={t('home.correction.notChanged')}
                onPress={() => openPicker('clockOut')}
                onClear={clockOut ? () => setClockOut(null) : undefined}
              />
            </View>
            <Text
              accessibilityLiveRegion={visibleErrors.times ? 'polite' : 'none'}
              style={[
                theme.typography.footnote,
                { color: visibleErrors.times ? theme.colors.status.danger.fg : theme.colors.textMuted },
              ]}
            >
              {visibleErrors.times ? t(ERROR_KEY[visibleErrors.times]) : t('home.correction.timesHelp')}
            </Text>
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
              {t('home.correction.timeZoneNote', { zone: f.timeZone })}
            </Text>

            <GlassTextField
              label={t('home.correction.reason')}
              icon="chatbox-ellipses-outline"
              value={reason}
              onChangeText={setReason}
              placeholder={t('home.correction.reasonPlaceholder')}
              multiline
              textAlignVertical="top"
              error={visibleErrors.reason ? t(ERROR_KEY[visibleErrors.reason]) : undefined}
              style={styles.reasonInput}
            />
          </Surface>

          <Text style={[theme.typography.footnote, styles.footer, { color: theme.colors.textMuted }]}>
            {t('home.correction.footer')}
          </Text>
        </View>
      </ScrollView>

      <StickyFooter>
        <LiquidButton
          label={t('home.correction.submit')}
          icon="paper-plane-outline"
          onPress={() => void onSubmit()}
          loading={submitting}
          disabled={submitting}
        />
      </StickyFooter>

      {Platform.OS === 'ios' ? (
        <BottomSheet
          visible={iosPicker !== null}
          onClose={() => setIosPicker(null)}
          title={iosPicker?.field === 'date'
            ? t('home.correction.date')
            : iosPicker?.field === 'clockIn' ? t('home.correction.clockIn') : t('home.correction.clockOut')}
          actions={
            <LiquidButton
              label={t('home.correction.done')}
              onPress={() => {
                if (iosPicker) apply(iosPicker.field, iosPicker.value);
                setIosPicker(null);
              }}
            />
          }
        >
          {iosPicker ? (
            <DateTimePicker
              value={iosPicker.value}
              mode={iosPicker.field === 'date' ? 'date' : 'time'}
              display={iosPicker.field === 'date' ? 'inline' : 'spinner'}
              maximumDate={iosPicker.field === 'date' && today ? pickerDate(today) : undefined}
              themeVariant={theme.isDark ? 'dark' : 'light'}
              accentColor={theme.colors.accent}
              onChange={(_event, picked) => {
                if (picked) setIosPicker((current) => (current ? { ...current, value: picked } : current));
              }}
            />
          ) : null}
        </BottomSheet>
      ) : null}
    </KeyboardAvoidingView>
  );
}

function PickerField({
  label,
  icon,
  value,
  placeholder,
  error,
  onPress,
  onClear,
  style,
}: {
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  value: string | null;
  placeholder: string;
  error?: string;
  onPress: () => void;
  onClear?: () => void;
  style?: object;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  return (
    <View style={[styles.field, style]}>
      <Text style={[theme.typography.footnote, styles.strong, { color: theme.colors.textSecondary }]}>{label}</Text>
      <View style={styles.fieldRow}>
        <MotionPressable
          onPress={onPress}
          haptic="selection"
          accessibilityRole="button"
          accessibilityLabel={`${label}: ${value ?? placeholder}`}
          style={styles.flex}
          contentStyle={[
            styles.fieldBody,
            {
              backgroundColor: theme.colors.surfaceInset,
              borderColor: error ? theme.colors.status.danger.fg : theme.colors.hairline,
            },
          ]}
        >
          <Ionicons accessible={false} name={icon} size={18} color={theme.colors.textMuted} />
          <Text
            numberOfLines={1}
            style={[theme.typography.callout, styles.flex, { color: value ? theme.colors.text : theme.colors.textMuted }]}
          >
            {value ?? placeholder}
          </Text>
        </MotionPressable>
        {onClear ? (
          <MotionPressable
            onPress={onClear}
            accessibilityRole="button"
            accessibilityLabel={t('home.correction.clearA11y', { field: label })}
            contentStyle={styles.clearButton}
          >
            <Ionicons accessible={false} name="close-circle" size={20} color={theme.colors.textMuted} />
          </MotionPressable>
        ) : null}
      </View>
      {error ? (
        <Text accessibilityLiveRegion="polite" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
          {error}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 24 },
  content: { paddingHorizontal: 16, gap: 16 },
  flex: { flex: 1, minWidth: 0 },
  strong: { fontWeight: '600' },
  infoCard: { flexDirection: 'row', alignItems: 'flex-start', gap: 10, padding: 14 },
  formCard: { padding: 16, gap: 12 },
  timeRow: { flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  field: { gap: 6, minWidth: 140 },
  fieldRow: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  fieldBody: {
    minHeight: 48,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: StyleSheet.hairlineWidth,
  },
  clearButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  reasonInput: { minHeight: 100, paddingTop: 14 },
  footer: { textAlign: 'center', paddingHorizontal: 12 },
});
